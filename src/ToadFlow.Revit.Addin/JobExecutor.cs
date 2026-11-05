using System.Collections.Concurrent;
using System.IO;
using Alexpivo394.Revit.Export.Core;
using Alexpivo394.Revit.Export.Services;
using Alexpivo394.Revit.ParamCheck.Profiles;
using Alexpivo394.Revit.ParamCheck.Reports;
using Alexpivo394.Revit.ParamCheck.Services;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Events;
using Autodesk.Revit.UI;
using ToadFlow.Contracts;
using ToadFlow.Contracts.Export;
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

    private string? _startupNote;

    /// <summary>Строка о запуске Revit (прогрев), которую первое задание запишет в свой журнал. Пишут поток Revit или таймер.</summary>
    public void SetStartupNote(string note) => Interlocked.Exchange(ref _startupNote, note);

    public void Execute(UIApplication application)
    {
        while (_jobs.TryDequeue(out var job)) Run(application, job);
    }

    public string GetName() => "ToadFlow: задание над моделью";

    private void Run(UIApplication application, JobMessage job)
    {
        progress.Begin(job.JobId);
        if (Interlocked.Exchange(ref _startupNote, null) is { } startup) progress.Step("", startup);
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
                case ModuleKinds.ClashExtract:
                    var (outcome, note, resultJson) = new ClashExtractRunner(revitYear, progress).Run(application, job);
                    Result(job, outcome, note, resultJson);
                    break;
                case "" or ModuleKinds.Export:
                    Export(application, job);
                    break;
                default:
                    // Аддин старее агента (обновление ещё не заменило DLL в Revit): модель тут ни при чём — повтор пойдёт в новом Revit,
                    // который загрузит новый аддин.
                    recycle = $"аддин не знает задания «{job.ModuleKind}» — загружена старая версия";
                    Result(job, JobOutcome.InfrastructureFailure, $"Аддин не умеет задания «{job.ModuleKind}» — обновите агента ToadFlow", null);
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
        var staging = TempWorkspace.Create();
        try
        {
            progress.Status("Начало выгрузки");
            var viewName = modelJob.Nwc ? options.NwcViewName : null;
            var (model, facts) = ExportOnce(application, modelJob, options, staging.Directory, viewName);
            if (EmptyView.RetryNwc(model) && !progress.ShouldAbort)
            {
                // Первая модель в только что запущенном Revit часто открывается с пустым видом, а следующая в том же Revit —
                // нормально (см. EmptyView). Повторяем здесь; не помогло — новый Revit, как раньше.
                if (facts is not null) progress.Step(payload.ModelName, EmptyView.Describe(facts));
                progress.Step(payload.ModelName, $"NWC не получился ({model.Note}) — повтор в этом же Revit");
                staging.Dispose();
                staging = TempWorkspace.Create();
                (model, facts) = ExportOnce(application, modelJob, options, staging.Directory, viewName);
                if (EmptyView.RetryNwc(model) && facts is not null) progress.Step(payload.ModelName, EmptyView.Describe(facts));
            }

            Publish(job, payload, model, staging.Directory);
        }
        finally
        {
            staging.Dispose();
        }
    }

    /// <summary>
    ///     Одна выгрузка модели. Модель открывает и закрывает <see cref="ModelExporter" />, поэтому подробности о виде NWC
    ///     (<see cref="ViewFacts" />) снимаются при её открытии — пригодятся, если NWC не получится.
    /// </summary>
    /// <param name="viewName">Вид NWC; null — NWC не нужен, подробности не собираются.</param>
    private (ModelResult Model, ViewFacts? Facts) ExportOnce(UIApplication application, ModelJob modelJob, ExportOptions options, string folder,
        string? viewName)
    {
        ViewFacts? facts = null;
        var captured = false;

        void OnOpened(object? sender, DocumentOpenedEventArgs args)
        {
            // Первый открытый проект — выгружаемая модель; дальше Revit может открыть её ещё раз при сохранении.
            if (captured || viewName is null || args.Document is not { IsFamilyDocument: false, IsLinked: false } document) return;
            captured = true;
            try
            {
                facts = ViewFactsReader.Read(document, viewName);
            }
            catch (Autodesk.Revit.Exceptions.ApplicationException)
            {
                // подробности не обязательны
            }
        }

        application.Application.DocumentOpened += OnOpened;
        try
        {
            var model = new ModelExporter(application.Application).Export(modelJob, options, folder, progress, progress);
            return (model, facts);
        }
        finally
        {
            application.Application.DocumentOpened -= OnOpened;
        }
    }

    private void Publish(JobMessage job, ExportJobPayload payload, ModelResult model, string folder)
    {
        if (model.Rvt == StepResult.Done || model.Nwc == StepResult.Done) progress.Status("Публикация результатов");
        ResultPublisher.PublishOutputs(model, payload, folder, text => progress.Step(payload.ModelName, text));

        var (outcome, result) = PayloadMapping.ToResult(model, payload, job.CanRetry);
        var note = outcome == JobOutcome.InfrastructureFailure && model is { NwcMissing: true, RevitFailure: false }
            ? $"NWC не получился, модель повторится в новом Revit: {model.Note}"
            : model.Note;
        Result(job, outcome, note, AddinPipeJson.SerializeObject(result));
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
