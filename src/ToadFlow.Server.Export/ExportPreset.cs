using System.Text.Json;
using ToadFlow.Contracts.Export;
using ToadFlow.Server.Data;

namespace ToadFlow.Server.Export;

/// <summary>Готовый набор настроек RVT/NWC. Расписание берёт пресет и при необходимости переопределяет отдельные поля.</summary>
public sealed class ExportPreset
{
    public Guid Id { get; set; }
    public string Name { get; set; } = "";
    public bool IsDefault { get; set; }
    public ExportSettingsDto Settings { get; set; } = new();
}

/// <summary>Настройки модуля в расписании (<c>Schedule.ModuleSettingsJson</c>).</summary>
public sealed record ExportScheduleSettings(Guid PresetId, ExportSettingsOverrides Overrides)
{
    public static ExportScheduleSettings? TryParse(JsonElement json)
    {
        try
        {
            var settings = json.Deserialize<ExportScheduleSettings>(ToadFlowDb.Json);
            return settings is null ? null : settings with { Overrides = settings.Overrides ?? new ExportSettingsOverrides() };
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public static ExportScheduleSettings? TryParse(string json)
    {
        using var document = JsonDocument.Parse(string.IsNullOrWhiteSpace(json) ? "{}" : json);
        return TryParse(document.RootElement);
    }
}

public static class SettingsMerge
{
    /// <summary>Настройки пресета, где заданные в <paramref name="overrides" /> поля заменены.</summary>
    public static ExportSettingsDto Apply(ExportSettingsDto preset, ExportSettingsOverrides overrides) => preset with
    {
        SkipLinks = overrides.SkipLinks ?? preset.SkipLinks,
        Audit = overrides.Audit ?? preset.Audit,
        WorksetKeyword = overrides.WorksetKeyword ?? preset.WorksetKeyword,
        DeleteRevitLinks = overrides.DeleteRevitLinks ?? preset.DeleteRevitLinks,
        DeleteCad = overrides.DeleteCad ?? preset.DeleteCad,
        DeletePointClouds = overrides.DeletePointClouds ?? preset.DeletePointClouds,
        Purge = overrides.Purge ?? preset.Purge,
        Compact = overrides.Compact ?? preset.Compact,
        NwcViewName = overrides.NwcViewName ?? preset.NwcViewName,
        NwcSharedCoordinates = overrides.NwcSharedCoordinates ?? preset.NwcSharedCoordinates,
        NwcConvertElementProperties = overrides.NwcConvertElementProperties ?? preset.NwcConvertElementProperties,
        NwcParameters = overrides.NwcParameters ?? preset.NwcParameters,
        NwcDivideIntoLevels = overrides.NwcDivideIntoLevels ?? preset.NwcDivideIntoLevels,
        NwcExportLinks = overrides.NwcExportLinks ?? preset.NwcExportLinks,
        NwcConvertLinkedCad = overrides.NwcConvertLinkedCad ?? preset.NwcConvertLinkedCad,
        NwcExportParts = overrides.NwcExportParts ?? preset.NwcExportParts,
        NwcRoomGeometry = overrides.NwcRoomGeometry ?? preset.NwcRoomGeometry,
        NwcRoomAsAttribute = overrides.NwcRoomAsAttribute ?? preset.NwcRoomAsAttribute,
        NwcConvertLights = overrides.NwcConvertLights ?? preset.NwcConvertLights,
        NwcExportElementIds = overrides.NwcExportElementIds ?? preset.NwcExportElementIds,
        NwcExportUrls = overrides.NwcExportUrls ?? preset.NwcExportUrls,
        NwcFindMissingMaterials = overrides.NwcFindMissingMaterials ?? preset.NwcFindMissingMaterials,
        NwcFacetingFactor = overrides.NwcFacetingFactor ?? preset.NwcFacetingFactor
    };
}
