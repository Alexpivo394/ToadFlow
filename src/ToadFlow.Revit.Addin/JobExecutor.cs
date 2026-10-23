using System.Collections.Concurrent;
using Alexpivo394.Revit.Export.Core;
using Alexpivo394.Revit.Export.Services;
using Autodesk.Revit.UI;
using ToadFlow.Contracts;
using ToadFlow.Contracts.Pipe;
using ToadFlow.Revit.Addin.Logic;

namespace ToadFlow.Revit.Addin;

/// <summary>Выгрузка в потоке Revit API: задание от агента → <see cref="ModelExporter" /> → результат агенту.</summary>
internal sealed class JobExecutor(int revitYear, PipeProgress progress) : IExternalEventHandler
{
    private readonly ConcurrentQueue<JobMessage> _jobs = new();

    /// <summary>Куда отправлять результат; задаёт клиент пайпа.</summary>
    public Action<PipeMessage>? Send { get; set; }

    public void Enqueue(JobMessage job) => _jobs.Enqueue(job);

    public void Execute(UIApplication application)
    {
        while (_jobs.TryDequeue(out var job)) Run(application, job);
    }

    public string GetName() => "ToadFlow: выгрузка модели";

    private void Run(UIApplication application, JobMessage job)
    {
        progress.Begin(job.JobId);
        try
        {
            var payload = PayloadMapping.ParsePayload(job.PayloadJson);
            if (PayloadMapping.CheckYear(payload, revitYear) is { } yearError)
            {
                Result(job, JobOutcome.Failed, yearError, null);
                return;
            }

            var options = PayloadMapping.ToOptions(payload.Settings);
            var modelJob = PayloadMapping.ToModelJob(payload);
            var errors = RunValidator.Errors([modelJob], options, payload.OutputFolder);
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
        catch (Exception exception)
        {
            Result(job, JobOutcome.Failed, exception.Message, null);
        }
    }

    private void Result(JobMessage job, JobOutcome outcome, string? note, string? resultJson)
    {
        progress.Touch();
        Send?.Invoke(new ResultMessage { JobId = job.JobId, Outcome = outcome, Note = note, ResultJson = resultJson });
    }
}
