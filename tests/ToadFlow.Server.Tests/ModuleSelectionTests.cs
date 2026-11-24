using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Shouldly;
using ToadFlow.Contracts;
using ToadFlow.Server.Clash;
using ToadFlow.Server.Modules;
using ToadFlow.Server.Tests.Infrastructure;

namespace ToadFlow.Server.Tests;

public class ModuleSelectionTests : ServerTest
{
    [Test]
    public async Task NoSetting_AllEnabled()
    {
        var modules = await (await ClientAsync(UserRole.Viewer)).GetJsonAsync<ModuleView[]>("/api/modules");

        modules.Select(m => (m.Name, m.Enabled)).ShouldBe([("export", true), ("loi", true), ("clash", true), ("families", true), ("usage", true)]);
        modules.Select(m => m.Title).ShouldBe(["Выгрузка", "Проверка LOI", "Коллизии", "Семейства", "Аналитика"]);
    }

    [Test]
    public async Task NoSetting_DesignerSeesModules()
    {
        var response = await (await ClientAsync(UserRole.Designer)).GetAsync("/api/modules");

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Test]
    public async Task Health_ListsEnabledModules()
    {
        var body = JsonDocument.Parse(await Server.CreateClient().GetStringAsync("/api/health")).RootElement;

        body.GetProperty("modules").EnumerateArray().Select(m => m.GetString()).ShouldBe(["export", "loi", "clash", "families", "usage"]);
    }
}

public class ModuleSelectionDisabledTests : ServerTest
{
    public ModuleSelectionDisabledTests() => Modules = "Export, LOI,";

    [Test]
    public async Task Disabled_RoutesAreAbsent()
    {
        var admin = await AdminAsync();

        (await admin.GetAsync("/api/clash/checks")).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await admin.GetAsync("/api/export/presets")).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Test]
    public async Task Disabled_TablesStillExist()
    {
        var count = await DbAsync(db => db.Set<ClashModel>().CountAsync());

        count.ShouldBe(0);
    }

    [Test]
    public async Task Disabled_ListedAsDisabled()
    {
        var modules = await (await AdminAsync()).GetJsonAsync<ModuleView[]>("/api/modules");

        modules.Select(m => (m.Name, m.Enabled)).ShouldBe([("export", true), ("loi", true), ("clash", false), ("families", false), ("usage", false)]);
    }

    [Test]
    public async Task Disabled_HealthHasNoClashProbe()
    {
        var body = JsonDocument.Parse(await Server.CreateClient().GetStringAsync("/api/health")).RootElement;

        body.TryGetProperty("clashVolumeFreeGb", out _).ShouldBeFalse();
        body.GetProperty("modules").EnumerateArray().Select(m => m.GetString()).ShouldBe(["export", "loi"]);
    }
}

public class ModuleSettingParseTests
{
    [ClassDataSource<PostgresFixture>(Shared = SharedType.PerTestSession)]
    public required PostgresFixture Postgres { get; init; }

    [Test]
    [Arguments(null)]
    [Arguments("")]
    [Arguments("  ")]
    public void Empty_MeansAll(string? value) => ModuleSet.ParseSetting(value).ShouldBeNull();

    [Test]
    public void Tolerant() => ModuleSet.ParseSetting(" Export, LOI,,loi ").ShouldNotBeNull().Order().ShouldBe(["export", "loi"]);

    [Test]
    public async Task UnknownName_FailsStartWithNames()
    {
        var connectionString = await Postgres.CreateDatabaseAsync();
        await using var server = new ServerFactory(connectionString, modules: "exprot");

        var error = Should.Throw<InvalidOperationException>(() => server.Server);

        error.Message.ShouldContain("exprot");
        error.Message.ShouldContain("export, loi, clash");
        Postgres.DropDatabaseLater(connectionString);
    }
}
