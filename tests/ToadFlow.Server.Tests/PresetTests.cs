using System.Net;
using Shouldly;
using ToadFlow.Contracts;
using ToadFlow.Contracts.Export;
using ToadFlow.Server.Tests.Infrastructure;

namespace ToadFlow.Server.Tests;

public sealed record PresetView(Guid Id, string Name, bool IsDefault, ExportSettingsDto Settings);

public class PresetTests : ServerTest
{
    [Test]
    public async Task Presets_DefaultSeeded()
    {
        var presets = await (await ClientAsync(UserRole.Viewer)).GetJsonAsync<PresetView[]>("/api/export/presets");

        var preset = presets.ShouldHaveSingleItem();
        preset.Name.ShouldBe("Стандарт");
        preset.IsDefault.ShouldBeTrue();
        preset.Settings.ShouldBe(new ExportSettingsDto());
    }

    [Test]
    public async Task Presets_OperatorCreates()
    {
        var operatorClient = await ClientAsync(UserRole.Operator);

        var response = await operatorClient.PostJsonAsync("/api/export/presets",
            new { name = "Для заказчика", isDefault = false, settings = new ExportSettingsDto { DeletePointClouds = true } });

        response.StatusCode.ShouldBe(HttpStatusCode.Created);
        (await operatorClient.GetJsonAsync<PresetView[]>("/api/export/presets")).Length.ShouldBe(2);
    }

    [Test]
    public async Task Presets_ViewerCannotCreate_403()
    {
        var viewer = await ClientAsync(UserRole.Viewer);

        (await viewer.PostJsonAsync("/api/export/presets", new { name = "X", isDefault = false, settings = new ExportSettingsDto() }))
            .StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Test]
    public async Task Presets_CannotDeleteDefault_409()
    {
        var operatorClient = await ClientAsync(UserRole.Operator);
        var standard = (await operatorClient.GetJsonAsync<PresetView[]>("/api/export/presets")).Single();

        (await operatorClient.DeleteAsync($"/api/export/presets/{standard.Id}")).StatusCode.ShouldBe(HttpStatusCode.Conflict);
    }

    [Test]
    public async Task Presets_NewDefaultUnsetsOld()
    {
        var operatorClient = await ClientAsync(UserRole.Operator);

        await operatorClient.PostJsonAsync("/api/export/presets", new { name = "Новый", isDefault = true, settings = new ExportSettingsDto() });

        var presets = await operatorClient.GetJsonAsync<PresetView[]>("/api/export/presets");
        presets.Single(p => p.IsDefault).Name.ShouldBe("Новый");
    }

    [Test]
    public async Task Presets_DuplicateName_409()
    {
        var operatorClient = await ClientAsync(UserRole.Operator);

        (await operatorClient.PostJsonAsync("/api/export/presets", new { name = "Стандарт", isDefault = false, settings = new ExportSettingsDto() }))
            .StatusCode.ShouldBe(HttpStatusCode.Conflict);
    }

    [Test]
    public async Task Presets_UsedBySchedule_DeleteConflict()
    {
        var operatorClient = await ClientAsync(UserRole.Operator);
        var created = await (await operatorClient.PostJsonAsync("/api/export/presets",
            new { name = "Особый", isDefault = false, settings = new ExportSettingsDto() })).ReadJsonAsync<PresetView>();
        await DbAsync(async db =>
        {
            var schedule = Seed.Schedule(db, Seed.Source(db).Id);
            schedule.ModuleSettingsJson = $$$"""{"presetId":"{{{created.Id}}}","overrides":{}}""";
            await db.SaveChangesAsync();
        });

        (await operatorClient.DeleteAsync($"/api/export/presets/{created.Id}")).StatusCode.ShouldBe(HttpStatusCode.Conflict);
    }
}
