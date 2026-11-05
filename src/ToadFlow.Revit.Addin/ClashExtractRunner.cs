using System.IO;
using Alexpivo394.Revit.Clash.Geometry;
using Alexpivo394.Revit.Clash.Revit;
using Alexpivo394.Revit.Export.Core;
using Alexpivo394.Revit.Export.Services;
using Autodesk.Revit.UI;
using ToadFlow.Contracts;
using ToadFlow.Contracts.Clash;
using ToadFlow.Contracts.Pipe;
using ToadFlow.Revit.Addin.Logic;

namespace ToadFlow.Revit.Addin;

/// <summary>
///     Выгрузка для коллизий в потоке Revit API: открыть модель (<see cref="ModelOpener" />), выгрузить видимые на виде
///     элементы (<see cref="GeometryExtractor" />), закрыть без сохранения и записать слепок в <see cref="ClashStaging" />.
///     Загружает слепок на сервер агент.
/// </summary>
internal sealed class ClashExtractRunner(int revitYear, PipeProgress progress)
{
    public (JobOutcome Outcome, string? Note, string? ResultJson) Run(UIApplication application, JobMessage job)
    {
        var payload = ClashMapping.ParsePayload(job.PayloadJson);
        if (ClashMapping.CheckYear(payload.RevitYear, revitYear) is { } yearError) return (JobOutcome.Failed, yearError, null);

        try
        {
            var (result, facts) = Extract(application, payload);
            if (EmptyView.IsEmpty(result.Geometry.Elements.Count, result.Geometry.Inventory.Count))
            {
                // Первая модель в только что запущенном Revit часто открывается с пустым видом, а следующая в том же Revit —
                // нормально (см. EmptyView). Повторяем здесь, не перезапуская Revit.
                if (facts is not null) progress.Step(payload.ModelName, EmptyView.Describe(facts));
                progress.Step(payload.ModelName, "на виде нет видимых элементов — повтор в этом же Revit");
                if (progress.ShouldAbort) throw new OperationCanceledException();
                (result, facts) = Extract(application, payload);
                if (EmptyView.IsEmpty(result.Geometry.Elements.Count, result.Geometry.Inventory.Count))
                {
                    if (facts is not null) progress.Step(payload.ModelName, EmptyView.Describe(facts));
                    var (emptyOutcome, emptyNote) = EmptyView.ClashOutcome(job.CanRetry, payload.ViewName, result.Geometry.Inventory.Count);
                    progress.Step(payload.ModelName, emptyNote);
                    return (emptyOutcome, emptyNote, null);
                }
            }

            var (geometry, parameters) = (result.Geometry, result.Parameters);
            foreach (var line in ClashMapping.DescribeReport(result)) progress.Step(payload.ModelName, line);

            progress.Status("Запись слепка");
            var folder = ClashStaging.Folder(job.JobId, job.Attempt);
            if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true); // осталась от прошлой попытки в этом Revit
            ModelSnapshotFiles.Write(folder, geometry, parameters);

            var triangles = geometry.Elements.Sum(e => (long)e.Mesh.TriangleCount);
            var megabytes = Directory.EnumerateFiles(folder).Sum(f => new FileInfo(f).Length) / (1024.0 * 1024.0);
            progress.Step(payload.ModelName,
                $"слепок записан: элементов {geometry.Elements.Count}, треугольников {triangles}, в описи {geometry.Inventory.Count}, {megabytes:0.#} МБ");
            var staged = ClashMapping.ToStaged(folder, geometry.Elements.Count, triangles, geometry.Inventory.Count);
            return (JobOutcome.Succeeded, null, AddinPipeJson.SerializeObject(staged));
        }
        catch (Exception exception) when (exception is OperationCanceledException or ArgumentException or IOException or UnauthorizedAccessException or OutOfMemoryException
                                              or InvalidOperationException or Autodesk.Revit.Exceptions.ApplicationException)
        {
            progress.Step(payload.ModelName, ClashMapping.FailureLog(exception));
            var (outcome, note) = ClashMapping.MapFailure(exception, exception is Autodesk.Revit.Exceptions.InternalException, payload.ViewName);
            return (outcome, note, null);
        }
    }

    /// <returns>Итог выгрузки и, если вид оказался пустым, подробности о нём, собранные до закрытия модели.</returns>
    private (ExtractResult Result, ViewFacts? Facts) Extract(UIApplication application, ClashExtractPayload payload)
    {
        using var temp = TempWorkspace.Create();
        var opened = new ModelOpener(application.Application).Open(ClashMapping.ToModelJob(payload), ClashMapping.ToOpenOptions(payload), temp, progress,
            progress);
        try
        {
            progress.Status("Выгрузка геометрии");
            using var abort = new CancellationTokenSource();
            var reporter = new StepReporter(progress, payload.ModelName, abort);
            if (progress.ShouldAbort) abort.Cancel();
            var result = GeometryExtractor.Extract(opened.Document, new ExtractOptions(payload.ModelKey, payload.ViewName), reporter, abort.Token);
            var facts = EmptyView.IsEmpty(result.Geometry.Elements.Count, result.Geometry.Inventory.Count)
                ? ViewFactsReader.Read(opened.Document, payload.ViewName)
                : null;
            return (result, facts);
        }
        finally
        {
            opened.Document.Close(false);
        }
    }

    /// <summary>Ход выгрузки — в журнал задания; заодно переводит отмену пользователя в отмену выгрузки.</summary>
    private sealed class StepReporter(PipeProgress progress, string model, CancellationTokenSource abort) : IProgress<ExtractProgress>
    {
        public void Report(ExtractProgress value)
        {
            if (progress.ShouldAbort) abort.Cancel();
            progress.Step(model, ClashMapping.Describe(value));
        }
    }
}
