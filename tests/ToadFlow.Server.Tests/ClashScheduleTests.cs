using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Shouldly;
using ToadFlow.Contracts;
using ToadFlow.Contracts.Clash;
using ToadFlow.Server.Clash;
using ToadFlow.Server.Data.Entities;
using ToadFlow.Server.Tests.Infrastructure;

namespace ToadFlow.Server.Tests;

public class ClashScheduleTests : ServerTest
{
    private static object Body(Guid sourceId, object moduleSettings, string moduleKind = ModuleKinds.ClashExtract, string outputFolder = "",
        params string[] models) => new
    {
        name = "Коллизии ЖК Север",
        moduleKind,
        sourceId,
        outputFolderTemplate = outputFolder,
        selection = new SelectionDto((models.Length == 0 ? ["АР.rvt"] : models).Select(m => new ModelSelectionDto(m, $"RSN://rs22/ЖК/{m}", true, false)).ToArray(), []),
        moduleSettings,
        recurrenceKind = RecurrenceKind.Weekly,
        daysOfWeekMask = 0b0011111,
        timeOfDay = "20:00:00",
        intervalHours = (int?)null,
        onceAt = (DateTimeOffset?)null,
        priority = 0,
        modelTimeoutMinutes = 60,
        maxRetries = 1,
        isEnabled = true
    };

    private async Task<List<Job>> RunAsync(HttpClient admin, Guid scheduleId)
    {
        await using var agent = await FakeAgent.RegisterAsync(Server, admin);
        agent.ExpandSelection = (_, selection) => selection.Models.Select(m => new ExpandedModelDto(m.Name, m.ModelPath, true, null, m.Rvt, m.Nwc)).ToArray();
        await agent.ConnectAsync();
        var runId = (await (await admin.PostAsync($"/api/schedules/{scheduleId}/run", null)).ReadJsonAsync<RunCreated>()).RunId;
        await EventuallyAsync(async () => (await DbAsync(db => db.Runs.SingleAsync(r => r.Id == runId))).State != RunState.Expanding);
        return await DbAsync(db => db.Jobs.Where(j => j.RunId == runId).ToListAsync());
    }

    private static ClashExtractPayload PayloadOf(Job job) => JsonSerializer.Deserialize<ClashExtractPayload>(job.PayloadJson, TestJson.Options)!;

    [Test]
    public async Task CreateSchedule_ClashExtract_WithoutOutputFolder_Succeeds()
    {
        var admin = await AdminAsync();
        var sourceId = await ScheduleApi.SourceAsync(admin);

        var response = await admin.PostJsonAsync("/api/schedules", Body(sourceId, new { viewName = "Navisworks" }));

        response.StatusCode.ShouldBe(HttpStatusCode.Created);
    }

    [Test]
    public async Task CreateSchedule_ClashExtract_EmptyView_Rejected()
    {
        var admin = await AdminAsync();
        var sourceId = await ScheduleApi.SourceAsync(admin);

        var response = await admin.PostJsonAsync("/api/schedules", Body(sourceId, new { viewName = "  " }));

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync()).ShouldContain("Укажите имя 3D-вида для выгрузки");
    }

    [Test]
    public async Task ExportAndLoiSchedules_StillRequireOutputFolder()
    {
        var admin = await AdminAsync();
        var sourceId = await ScheduleApi.SourceAsync(admin);

        var loi = await admin.PostJsonAsync("/api/schedules", Body(sourceId, new { profileId = Guid.NewGuid() }, ModuleKinds.Loi));
        var export = await admin.PostJsonAsync("/api/schedules", Body(sourceId, new { presetId = Guid.NewGuid(), overrides = new { } }, ModuleKinds.Export));

        (await loi.Content.ReadAsStringAsync()).ShouldContain("Укажите папку результатов");
        (await export.Content.ReadAsStringAsync()).ShouldContain("Укажите папку результатов");
    }

    [Test]
    public async Task RunSchedule_BuildsPayloadWithStableModelKey()
    {
        var admin = await AdminAsync();
        var sourceId = await ScheduleApi.SourceAsync(admin);
        var schedule = await ScheduleApi.CreateAsync(admin, Body(sourceId, new { viewName = "Коллизии", worksetKeyword = "#Связи" }, models: ["АР.rvt", "ОВ.rvt"]));

        var first = await RunAsync(admin, schedule.Id);
        var second = await RunAsync(admin, schedule.Id);

        first.Count.ShouldBe(2);
        first.ShouldAllBe(j => j.ModuleKind == ModuleKinds.ClashExtract);
        var ar1 = PayloadOf(first.Single(j => j.ModelName == "АР.rvt"));
        var ar2 = PayloadOf(second.Single(j => j.ModelName == "АР.rvt"));
        var ov = PayloadOf(first.Single(j => j.ModelName == "ОВ.rvt"));
        ar1.ModelKey.ShouldBe(ar2.ModelKey);
        ar1.ModelKey.ShouldNotBe(ov.ModelKey);
        ar1.ModelKey.ShouldMatch("^[0-9a-f]{32}$");
        ar1.ViewName.ShouldBe("Коллизии");
        ar1.WorksetKeyword.ShouldBe("#Связи");
        ar1.ModelPath.ShouldBe("RSN://rs22/ЖК/АР.rvt");
        ar1.RevitYear.ShouldBe(2022);

        var models = await DbAsync(db => db.Set<ClashModel>().AsNoTracking().ToListAsync());
        models.Count.ShouldBe(2);
        models.Single(m => m.Name == "АР.rvt").Id.ToString("N").ShouldBe(ar1.ModelKey);
    }
}
