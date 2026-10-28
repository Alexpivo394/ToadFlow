using System.IO;
using Alexpivo394.Revit.Export.Core;
using ToadFlow.Contracts;
using ToadFlow.Contracts.Export;

namespace ToadFlow.Revit.Addin.Logic;

/// <summary>Задание ToadFlow ↔ типы библиотеки выгрузки. Без Revit API — проверяется тестами.</summary>
public static class PayloadMapping
{
    public static ExportJobPayload ParsePayload(string json) => AddinPipeJson.DeserializeObject<ExportJobPayload>(json);

    /// <summary>Ферма никогда не обновляет модели: <c>AllowUpgrade = false</c>.</summary>
    public static ExportOptions ToOptions(ExportSettingsDto s) => new()
    {
        SkipLinks = s.SkipLinks,
        Audit = s.Audit,
        WorksetKeyword = s.WorksetKeyword ?? "",
        AllowUpgrade = false,
        DeleteRevitLinks = s.DeleteRevitLinks,
        DeleteCad = s.DeleteCad,
        DeletePointClouds = s.DeletePointClouds,
        Purge = s.Purge,
        Compact = s.Compact,
        NwcViewName = s.NwcViewName ?? "",
        NwcSharedCoordinates = s.NwcSharedCoordinates,
        NwcConvertElementProperties = s.NwcConvertElementProperties,
        NwcParameters = s.NwcParameters switch
        {
            Contracts.Export.NwcParameters.None => Alexpivo394.Revit.Export.Core.NwcParameters.None,
            Contracts.Export.NwcParameters.Elements => Alexpivo394.Revit.Export.Core.NwcParameters.Elements,
            _ => Alexpivo394.Revit.Export.Core.NwcParameters.All
        },
        NwcDivideIntoLevels = s.NwcDivideIntoLevels,
        NwcExportLinks = s.NwcExportLinks,
        NwcConvertLinkedCad = s.NwcConvertLinkedCad,
        NwcExportParts = s.NwcExportParts,
        NwcRoomGeometry = s.NwcRoomGeometry,
        NwcRoomAsAttribute = s.NwcRoomAsAttribute,
        NwcConvertLights = s.NwcConvertLights,
        NwcExportElementIds = s.NwcExportElementIds,
        NwcExportUrls = s.NwcExportUrls,
        NwcFindMissingMaterials = s.NwcFindMissingMaterials,
        NwcFacetingFactor = s.NwcFacetingFactor
    };

    public static ModelJob ToModelJob(ExportJobPayload payload) => new(payload.ModelName, payload.ModelPath, payload.IsServer, payload.Rvt, payload.Nwc);

    /// <summary>Ошибка, если задание для другой версии Revit; null — версия та.</summary>
    public static string? CheckYear(ExportJobPayload payload, int revitYear) =>
        payload.RevitYear == revitYear ? null : $"Задание для Revit {payload.RevitYear}, а этот экземпляр — Revit {revitYear}";

    /// <summary>Итог выгрузки → итог задания и результат для истории (статусы форматов, размеры и пути файлов).</summary>
    /// <param name="canRetry">
    ///     У задания остались попытки: NWC, который не получился, хотя модель открылась (<see cref="ModelResult.NwcMissing" />), —
    ///     сбой инфраструктуры, сервис повторит модель в новом Revit. На последней попытке остаётся итог как есть.
    /// </param>
    public static (JobOutcome Outcome, ExportJobResult Result) ToResult(ModelResult model, ExportJobPayload payload, bool canRetry = false)
    {
        var rvtPath = Path.Combine(payload.OutputFolder, payload.ModelName);
        var nwcPath = Path.Combine(payload.OutputFolder, Path.GetFileNameWithoutExtension(payload.ModelName) + ".nwc");
        var result = new ExportJobResult(
            Status(model.Rvt), Status(model.Nwc),
            model.Rvt == StepResult.Done ? Size(rvtPath) : null,
            model.Nwc == StepResult.Done ? Size(nwcPath) : null,
            model.Rvt == StepResult.Done ? rvtPath : null,
            model.Nwc == StepResult.Done ? nwcPath : null);
        // Внутренняя ошибка Revit — не вина модели: сбой инфраструктуры, сервис повторит модель в новом Revit.
        var retry = !model.SkippedByUser && (model.RevitFailure || (canRetry && model.NwcMissing));
        var outcome = retry ? JobOutcome.InfrastructureFailure : ExportOutcome.From(result, model.SkippedByUser);
        return (outcome, result);
    }

    private static StepStatus Status(StepResult step) => step switch
    {
        StepResult.Done => StepStatus.Done,
        StepResult.Skipped => StepStatus.Skipped,
        StepResult.Failed => StepStatus.Failed,
        _ => StepStatus.NotRequested
    };

    private static long? Size(string path)
    {
        try
        {
            return File.Exists(path) ? new FileInfo(path).Length : null;
        }
        catch (IOException)
        {
            return null;
        }
    }
}
