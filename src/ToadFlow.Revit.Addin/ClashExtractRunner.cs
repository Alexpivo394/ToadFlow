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
            var (geometry, parameters) = Extract(application, payload);

            progress.Status("Запись слепка");
            var folder = ClashStaging.Folder(job.JobId);
            if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true); // осталась от прошлой попытки в этом Revit
            ModelSnapshotFiles.Write(folder, geometry, parameters);

            var triangles = geometry.Elements.Sum(e => (long)e.Mesh.TriangleCount);
            var megabytes = Directory.EnumerateFiles(folder).Sum(f => new FileInfo(f).Length) / (1024.0 * 1024.0);
            progress.Step(payload.ModelName,
                $"слепок записан: элементов {geometry.Elements.Count}, треугольников {triangles}, в описи {geometry.Inventory.Count}, {megabytes:0.#} МБ");
            var staged = ClashMapping.ToStaged(folder, geometry.Elements.Count, triangles, geometry.Inventory.Count);
            return (JobOutcome.Succeeded, null, AddinPipeJson.SerializeObject(staged));
        }
        catch (Exception exception) when (exception is OperationCanceledException or ArgumentException or IOException or UnauthorizedAccessException
                                              or InvalidOperationException or Autodesk.Revit.Exceptions.ApplicationException)
        {
            progress.Step(payload.ModelName, $"выгрузка не удалась: {exception}");
            var (outcome, note) = ClashMapping.MapFailure(exception, exception is Autodesk.Revit.Exceptions.InternalException, payload.ViewName);
            return (outcome, note, null);
        }
    }

    private (ModelGeometry Geometry, ModelParameters Parameters) Extract(UIApplication application, ClashExtractPayload payload)
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
            return GeometryExtractor.Extract(opened.Document, new ExtractOptions(payload.ModelKey, payload.ViewName), reporter, abort.Token);
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
