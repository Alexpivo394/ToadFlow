using System.Net;
using System.Text.Json;
using Shouldly;
using ToadFlow.Contracts;
using ToadFlow.Server.Modules;
using ToadFlow.Server.Tests.Infrastructure;

namespace ToadFlow.Server.Tests;

public class UsageModuleTests : ServerTest
{
    [Test]
    public async Task Modules_ListsUsage()
    {
        var modules = await (await ClientAsync(UserRole.Viewer)).GetJsonAsync<ModuleView[]>("/api/modules");

        modules.ShouldContain(new ModuleView("usage", "Аналитика", true));
    }

    [Test]
    [Arguments("usage")]
    [Arguments("")]
    public async Task Schedule_ForUsage_Rejected(string kind)
    {
        var admin = await AdminAsync();
        var sourceId = await ScheduleApi.SourceAsync(admin);
        var body = ScheduleApi.Body(sourceId, await ScheduleApi.DefaultPresetAsync(admin));
        var json = JsonSerializer.SerializeToNode(body, TestJson.Options)!;
        json["moduleKind"] = kind;

        var response = await admin.PostJsonAsync("/api/schedules", json);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Test]
    public async Task Overview_NoFailedJobsLine()
    {
        var body = JsonDocument.Parse(await (await AdminAsync()).GetStringAsync("/api/overview")).RootElement;

        body.GetProperty("attention").EnumerateArray()
            .ShouldNotContain(item => item.GetProperty("module").ValueKind == JsonValueKind.String && item.GetProperty("module").GetString() == "usage");
    }

    [Test]
    public async Task Health_ListsUsage()
    {
        var body = JsonDocument.Parse(await Server.CreateClient().GetStringAsync("/api/health")).RootElement;

        body.GetProperty("modules").EnumerateArray().Select(m => m.GetString()).ShouldContain("usage");
    }
}

public class UsageModuleDisabledTests : ServerTest
{
    public UsageModuleDisabledTests() => Modules = "export";

    [Test]
    public async Task Disabled_NoRoutes()
    {
        var response = await (await AdminAsync()).PostJsonAsync("/api/usage/events", new { version = 1, items = Array.Empty<object>() });

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Test]
    public async Task Disabled_ListedAsDisabled()
    {
        var modules = await (await AdminAsync()).GetJsonAsync<ModuleView[]>("/api/modules");

        modules.ShouldContain(new ModuleView("usage", "Аналитика", false));
    }
}
