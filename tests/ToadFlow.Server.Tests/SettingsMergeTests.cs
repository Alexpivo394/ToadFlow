using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using ToadFlow.Contracts.Export;
using ToadFlow.Server.Export;
using ToadFlow.Server.Modules;
using ToadFlow.Server.Tests.Infrastructure;

namespace ToadFlow.Server.Tests;

public class SettingsMergeTests
{
    [Test]
    public void Merge_OverrideReplacesOnlySetFields()
    {
        var preset = new ExportSettingsDto { Purge = true, NwcViewName = "Navisworks", DeleteCad = true };

        var merged = SettingsMerge.Apply(preset, new ExportSettingsOverrides { Purge = false, NwcViewName = "NW_Export" });

        merged.ShouldBe(preset with { Purge = false, NwcViewName = "NW_Export" });
    }

    [Test]
    public void Merge_EmptyOverrides_EqualsPreset()
    {
        var preset = new ExportSettingsDto { DeletePointClouds = true, NwcFacetingFactor = 2.5 };

        SettingsMerge.Apply(preset, new ExportSettingsOverrides()).ShouldBe(preset);
    }

    [Test]
    public void Merge_EveryOverrideFieldMapsToSettingsField()
    {
        // Новое поле в ExportSettingsDto без пары в ExportSettingsOverrides молча не переопределялось бы.
        var settings = typeof(ExportSettingsDto).GetProperties().Select(p => p.Name).Order();
        var overrides = typeof(ExportSettingsOverrides).GetProperties().Select(p => p.Name).Order();

        overrides.ShouldBe(settings);
    }
}

public class ExportModuleValidationTests : ServerTest
{
    [Test]
    public async Task Validate_UnknownPreset_Error()
    {
        var module = Server.Services.GetServices<IServerModule>().Single(m => m.Kind == "export");
        using var settings = JsonDocument.Parse($$$"""{"presetId":"{{{Guid.NewGuid()}}}","overrides":{}}""");

        var errors = await module.ValidateSettingsAsync(settings.RootElement, Server.Services, CancellationToken.None);

        errors.ShouldContain(e => e.Contains("Пресет"));
    }

    [Test]
    public async Task Validate_NwcWithoutViewName_Error()
    {
        var module = Server.Services.GetServices<IServerModule>().Single(m => m.Kind == "export");
        var presetId = (await (await ClientAsync(Contracts.UserRole.Viewer)).GetJsonAsync<PresetView[]>("/api/export/presets")).Single().Id;
        using var settings = JsonDocument.Parse($$$"""{"presetId":"{{{presetId}}}","overrides":{"nwcViewName":" "}}""");

        var errors = await module.ValidateSettingsAsync(settings.RootElement, Server.Services, CancellationToken.None);

        errors.ShouldContain(e => e.Contains("3D-вида"));
    }
}
