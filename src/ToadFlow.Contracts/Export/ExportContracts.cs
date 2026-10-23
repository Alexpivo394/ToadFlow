namespace ToadFlow.Contracts.Export;

/// <summary>
///     Настройки выгрузки — те же поля и значения по умолчанию, что у <c>Alexpivo394.Revit.Export.Core.ExportOptions</c>,
///     кроме <c>AllowUpgrade</c>: ферма никогда не обновляет модели.
/// </summary>
public sealed record ExportSettingsDto
{
    public bool SkipLinks { get; init; } = true;
    public bool Audit { get; init; }
    public string WorksetKeyword { get; init; } = "";

    public bool DeleteRevitLinks { get; init; } = true;
    public bool DeleteCad { get; init; } = true;
    public bool DeletePointClouds { get; init; }
    public bool Purge { get; init; } = true;
    public bool Compact { get; init; } = true;

    public string NwcViewName { get; init; } = "Navisworks";
    public bool NwcSharedCoordinates { get; init; } = true;
    public bool NwcConvertElementProperties { get; init; }
    public NwcParameters NwcParameters { get; init; } = NwcParameters.All;
    public bool NwcDivideIntoLevels { get; init; } = true;

    public bool NwcExportLinks { get; init; }
    public bool NwcConvertLinkedCad { get; init; } = true;
    public bool NwcExportParts { get; init; }
    public bool NwcRoomGeometry { get; init; } = true;
    public bool NwcRoomAsAttribute { get; init; } = true;
    public bool NwcConvertLights { get; init; }

    public bool NwcExportElementIds { get; init; } = true;
    public bool NwcExportUrls { get; init; } = true;
    public bool NwcFindMissingMaterials { get; init; } = true;
    public double NwcFacetingFactor { get; init; } = 1.0;
}

/// <summary>Отличия расписания от пресета: заданное поле заменяет поле пресета, null — как в пресете.</summary>
public sealed record ExportSettingsOverrides
{
    public bool? SkipLinks { get; init; }
    public bool? Audit { get; init; }
    public string? WorksetKeyword { get; init; }

    public bool? DeleteRevitLinks { get; init; }
    public bool? DeleteCad { get; init; }
    public bool? DeletePointClouds { get; init; }
    public bool? Purge { get; init; }
    public bool? Compact { get; init; }

    public string? NwcViewName { get; init; }
    public bool? NwcSharedCoordinates { get; init; }
    public bool? NwcConvertElementProperties { get; init; }
    public NwcParameters? NwcParameters { get; init; }
    public bool? NwcDivideIntoLevels { get; init; }

    public bool? NwcExportLinks { get; init; }
    public bool? NwcConvertLinkedCad { get; init; }
    public bool? NwcExportParts { get; init; }
    public bool? NwcRoomGeometry { get; init; }
    public bool? NwcRoomAsAttribute { get; init; }
    public bool? NwcConvertLights { get; init; }

    public bool? NwcExportElementIds { get; init; }
    public bool? NwcExportUrls { get; init; }
    public bool? NwcFindMissingMaterials { get; init; }
    public double? NwcFacetingFactor { get; init; }
}

public enum NwcParameters
{
    None,
    Elements,
    All
}

/// <summary>Задание агенту на одну модель.</summary>
public sealed record ExportJobPayload(
    string ModelName,
    string ModelPath,
    bool IsServer,
    int RevitYear,
    bool Rvt,
    bool Nwc,
    string OutputFolder,
    ExportSettingsDto Settings);

/// <summary>Что получилось с моделью: по формату — статус, размер и путь файла.</summary>
public sealed record ExportJobResult(
    StepStatus Rvt,
    StepStatus Nwc,
    long? RvtSizeBytes,
    long? NwcSizeBytes,
    string? RvtPath,
    string? NwcPath);

public static class ExportOutcome
{
    /// <summary>
    ///     Итог задания по результату выгрузки: прервано — «отменено»; запрошенный формат упал — «ошибка»;
    ///     что-то пропущено по известной причине — «с предупреждением»; иначе — «успешно».
    /// </summary>
    public static JobOutcome From(ExportJobResult result, bool skippedByUser)
    {
        if (skippedByUser) return JobOutcome.Cancelled;
        if (result.Rvt == StepStatus.Failed || result.Nwc == StepStatus.Failed) return JobOutcome.Failed;
        if (result.Rvt == StepStatus.Skipped || result.Nwc == StepStatus.Skipped) return JobOutcome.Warning;
        return JobOutcome.Succeeded;
    }
}
