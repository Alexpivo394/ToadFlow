using System.Collections.Concurrent;
using System.IO;
using Alexpivo394.Revit.Export.Core;
using Alexpivo394.Revit.Export.Services;
using Alexpivo394.Revit.ParamCheck.Profiles;
using Alexpivo394.Revit.ParamCheck.Reports;
using Alexpivo394.Revit.ParamCheck.Services;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using ToadFlow.Contracts;
using ToadFlow.Contracts.Pipe;
using ToadFlow.Revit.Addin.Logic;
using ExportValidator = Alexpivo394.Revit.Export.Core.RunValidator;
using ProfileValidator = Alexpivo394.Revit.ParamCheck.Profiles.RunValidator;

namespace ToadFlow.Revit.Addin;

/// <summary>
///     Задания в потоке Revit API: от агента → библиотека модуля (<see cref="ModelExporter" />, <see cref="ModelInspector" />,
///     <see cref="RevitCatalog" />) → результат агенту.
/// </summary>
internal sealed class JobExecutor(int revitYear, PipeProgress progress) : IExternalEventHandler
{
    /// <summary>Сколько ждать, пока базу для дашбордов дописывает другой экземпляр Revit.</summary>
    private static readonly TimeSpan HistoryWait = TimeSpan.FromMinutes(2);

    private readonly ConcurrentQueue<JobMessage> _jobs = new();
    private ResultMessage? _result;

    /// <summary>Куда отправлять результат; задаёт клиент пайпа.</summary>
    public Action<PipeMessage>? Send { get; set; }

    public void Enqueue(JobMessage job) => _jobs.Enqueue(job);

    public void Execute(UIApplication application)
    {
        while (_jobs.TryDequeue(out var job)) Run(application, job);
    }

    public string GetName() => "ToadFlow: задание над моделью";

    private void Run(UIApplication application, JobMessage job)
    {
        progress.Begin(job.JobId);
        _result = null;
        string? recycle = null;
        var openBefore = OpenModels(application);
        try
        {
            if (progress.ShouldAbort)
            {
                Result(job, JobOutcome.Cancelled, "отменено пользователем", null);
                return;
            }

            switch (job.ModuleKind)
            {
                case ModuleKinds.Loi:
                    Check(application, job);
                    break;
                case ModuleKinds.LoiCatalog:
                    ReadCatalog(application, job);
                    break;
                case "" or ModuleKinds.Export:
                    Export(application, job);
                    break;
                default:
                    Result(job, JobOutcome.Failed, $"Аддин не умеет задания «{job.ModuleKind}» — обновите агента ToadFlow", null);
                    break;
            }
        }
        catch (Exception exception)
        {
            // Исключение прошло мимо библиотеки модуля — в каком состоянии остался Revit, неизвестно.
            progress.Step("", $"задание упало: {exception}");
            recycle = $"неожиданная ошибка аддина ({exception.GetType().Name})";
            Result(job, JobOutcome.Failed, exception.Message, null);
        }
        finally
        {
            // Незакрытая модель держит память и может помешать следующему заданию — такой Revit агент перезапустит.
            var left = OpenModels(application) - openBefore;
            if (left > 0)
            {
                progress.Step("", $"после задания осталось открытых моделей: {left} — Revit будет перезапущен");
                recycle ??= "модель не закрылась после задания";
            }

            var result = _result ?? new ResultMessage { JobId = job.JobId, Outcome = JobOutcome.Failed, Note = "аддин не вернул итог задания" };
            result.RecycleReason = recycle;
            progress.Touch();
            Send?.Invoke(result);
        }
    }

    private void Export(UIApplication application, JobMessage job)
    {
        var payload = PayloadMapping.ParsePayload(job.PayloadJson);
        if (PayloadMapping.CheckYear(payload, revitYear) is { } yearError)
        {
            Result(job, JobOutcome.Failed, yearError, null);
            return;
        }

        var options = PayloadMapping.ToOptions(payload.Settings);
        var modelJob = PayloadMapping.ToModelJob(payload);
        var errors = ExportValidator.Errors([modelJob], options, payload.OutputFolder);
        if (errors.Count > 0)
        {
            Result(job, JobOutcome.Failed, string.Join("; ", errors), null);
            return;
        }

        // Revit пишет файлы в локальную папку задания, в папку результатов они попадают только готовыми (ResultPublisher):
        // зависший или упавший экспорт не трогает прошлую выгрузку. Папку с замком брошенной после сбоя уберёт CleanupUnused.
        using var staging = TempWorkspace.Create();
        progress.Status("Начало выгрузки");
        var model = new ModelExporter(application.Application).Export(modelJob, options, staging.Directory, progress, progress);

        if (model.Rvt == StepResult.Done || model.Nwc == StepResult.Done) progress.Status("Публикация результатов");
        ResultPublisher.PublishOutputs(model, payload, staging.Directory, text => progress.Step(payload.ModelName, text));

        var (outcome, result) = PayloadMapping.ToResult(model, payload);
        Result(job, outcome, model.Note, AddinPipeJson.SerializeObject(result));
    }

    private void Check(UIApplication application, JobMessage job)
    {
        var payload = LoiMapping.ParsePayload(job.PayloadJson);
        if (LoiMapping.CheckYear(payload.RevitYear, revitYear) is { } yearError)
        {
            Result(job, JobOutcome.Failed, yearError, null);
            return;
        }

        var profile = ProfileSerializer.DeserializeProfile(payload.ProfileJson);
        var problems = ProfileValidator.RuleProblems(profile);
        if (problems.Count > 0)
        {
            Result(job, JobOutcome.Failed, string.Join("; ", problems), null);
            return;
        }

        progress.Status("Начало проверки");
        var outcome = new ModelInspector(application.Application).Check(payload.ModelPath, profile, LoiMapping.ToOptions(payload.Settings),
            LoiMapping.ReportPath(payload), progress, progress);

        string? historyError = null;
        var history = payload.Settings.HistoryFile.Trim();
        if (history.Length > 0 && LoiMapping.GoesToHistory(outcome))
        {
            progress.Status("Запись базы для дашбордов");
            try
            {
                HistoryWriter.AppendShared(history, payload.RunStartedAt.DateTime, [outcome], HistoryWait);
                progress.Step(payload.ModelName, $"итоги дописаны в базу {history}");
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                historyError = exception.Message;
                progress.Step(payload.ModelName, $"база для дашбордов не дописана: {exception.Message}");
            }
        }

        var (jobOutcome, note, result) = LoiMapping.ToResult(outcome, ProfileSerializer.Fingerprint(profile), historyError);
        Result(job, jobOutcome, note, result is null ? null : AddinPipeJson.SerializeObject(result));
    }

    private void ReadCatalog(UIApplication application, JobMessage job)
    {
        var payload = LoiMapping.ParseCatalogPayload(job.PayloadJson);
        if (LoiMapping.CheckYear(payload.RevitYear, revitYear) is { } yearError)
        {
            Result(job, JobOutcome.Failed, yearError, null);
            return;
        }

        progress.Status("Чтение категорий и параметров");
        var snapshot = RevitCatalog.Read(application.Application);
        progress.Step("", $"категорий {snapshot.Categories.Count}, параметров {snapshot.Parameters.Count}" +
                          (snapshot.HasSharedParameterFile ? "" : "; ФОП не подключён — ADSK-параметров в подсказках не будет"));
        Result(job, JobOutcome.Succeeded, null, AddinPipeJson.SerializeObject(LoiMapping.ToCatalogResult(snapshot)));
    }

    /// <summary>Итог задания; уходит агенту в конце <see cref="Run" />, после проверки, что модели закрыты.</summary>
    private void Result(JobMessage job, JobOutcome outcome, string? note, string? resultJson) =>
        _result = new ResultMessage { JobId = job.JobId, Outcome = outcome, Note = note, ResultJson = resultJson };

    /// <summary>Открытые проекты и семейства, без загруженных связей.</summary>
    private static int OpenModels(UIApplication application) =>
        application.Application.Documents.Cast<Document>().Count(document => !document.IsLinked);
}
