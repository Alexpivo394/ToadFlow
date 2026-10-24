using System.Collections.Concurrent;
using System.IO;
using Alexpivo394.Revit.Export.Core;
using Alexpivo394.Revit.Export.Services;
using Alexpivo394.Revit.ParamCheck.Profiles;
using Alexpivo394.Revit.ParamCheck.Reports;
using Alexpivo394.Revit.ParamCheck.Services;
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
            Result(job, JobOutcome.Failed, exception.Message, null);
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

        progress.Status("Начало выгрузки");
        var model = new ModelExporter(application.Application).Export(modelJob, options, payload.OutputFolder, progress, progress);
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

    private void Result(JobMessage job, JobOutcome outcome, string? note, string? resultJson)
    {
        progress.Touch();
        Send?.Invoke(new ResultMessage { JobId = job.JobId, Outcome = outcome, Note = note, ResultJson = resultJson });
    }
}
