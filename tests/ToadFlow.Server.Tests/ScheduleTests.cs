using System.Net;
using Microsoft.EntityFrameworkCore;
using Shouldly;
using ToadFlow.Contracts;
using ToadFlow.Server.Tests.Infrastructure;

namespace ToadFlow.Server.Tests;

public class ScheduleTests : ServerTest
{
    private sealed record ChangeView(DateTimeOffset At, string UserName, string Summary);

    [Test]
    public async Task Create_ReturnsAuthorsAndNextRun()
    {
        var admin = await AdminAsync();
        var operatorClient = await ClientAsync(UserRole.Operator);
        var sourceId = await ScheduleApi.SourceAsync(admin);

        var schedule = await ScheduleApi.CreateAsync(operatorClient, ScheduleApi.Body(sourceId, await ScheduleApi.DefaultPresetAsync(admin)));

        schedule.CreatedBy.ShouldBe("Тест Operator");
        schedule.UpdatedBy.ShouldBe("Тест Operator");
        schedule.NextRunAt.ShouldNotBeNull().ShouldBeGreaterThan(DateTimeOffset.UtcNow);
        (await operatorClient.GetJsonAsync<ScheduleView[]>("/api/schedules")).ShouldHaveSingleItem().Id.ShouldBe(schedule.Id);
    }

    [Test]
    public async Task Create_ValidatesModuleSettings_400()
    {
        var admin = await AdminAsync();
        var sourceId = await ScheduleApi.SourceAsync(admin);

        var response = await admin.PostJsonAsync("/api/schedules", ScheduleApi.Body(sourceId, Guid.NewGuid()));

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync()).ShouldContain("Пресет");
    }

    [Test]
    public async Task Create_EmptySelection_400()
    {
        var admin = await AdminAsync();
        var sourceId = await ScheduleApi.SourceAsync(admin);

        var response = await admin.PostJsonAsync("/api/schedules",
            ScheduleApi.Body(sourceId, await ScheduleApi.DefaultPresetAsync(admin), selection: new SelectionDto([], [])));

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Test]
    public async Task Create_BadTemplate_400()
    {
        var admin = await AdminAsync();
        var sourceId = await ScheduleApi.SourceAsync(admin);
        var body = System.Text.Json.JsonSerializer.SerializeToNode(ScheduleApi.Body(sourceId, await ScheduleApi.DefaultPresetAsync(admin)), TestJson.Options)!;
        body["outputFolderTemplate"] = @"\\nas\exports\{проект}";

        var response = await admin.PostJsonAsync("/api/schedules", body);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync()).ShouldContain("{проект}");
    }

    [Test]
    public async Task Create_WarnsWhenNoAgentForYear()
    {
        var admin = await AdminAsync();
        var sourceId = await ScheduleApi.SourceAsync(admin, year: 2024);

        var schedule = await ScheduleApi.CreateAsync(admin, ScheduleApi.Body(sourceId, await ScheduleApi.DefaultPresetAsync(admin)));

        schedule.Warnings.ShouldContain(w => w.Contains("Revit 2024"));
    }

    [Test]
    public async Task Create_WarnsOnDuplicateNames()
    {
        var admin = await AdminAsync();
        var sourceId = await ScheduleApi.SourceAsync(admin);
        var selection = new SelectionDto(
            [new ModelSelectionDto("АР.rvt", "RSN://rs22/К1/АР.rvt", true, true), new ModelSelectionDto("ар.rvt", "RSN://rs22/К2/ар.rvt", true, true)], []);

        var schedule = await ScheduleApi.CreateAsync(admin, ScheduleApi.Body(sourceId, await ScheduleApi.DefaultPresetAsync(admin), selection: selection));

        schedule.Warnings.ShouldContain(w => w.Contains("одинаковые имена", StringComparison.OrdinalIgnoreCase) && w.Contains("АР.rvt"));
    }

    [Test]
    public async Task Update_WritesChangeSummaryAndAuthor()
    {
        var admin = await AdminAsync();
        var operatorClient = await ClientAsync(UserRole.Operator);
        var sourceId = await ScheduleApi.SourceAsync(admin);
        var presetId = await ScheduleApi.DefaultPresetAsync(admin);
        var schedule = await ScheduleApi.CreateAsync(admin, ScheduleApi.Body(sourceId, presetId));

        var response = await operatorClient.PutJsonAsync($"/api/schedules/{schedule.Id}",
            ScheduleApi.Body(sourceId, presetId, timeOfDay: "18:00:00", maxRetries: 2));

        response.EnsureSuccessStatusCode();
        var updated = await response.ReadJsonAsync<ScheduleView>();
        updated.CreatedBy.ShouldBe("Администратор");
        updated.UpdatedBy.ShouldBe("Тест Operator");
        var changes = await operatorClient.GetJsonAsync<ChangeView[]>($"/api/schedules/{schedule.Id}/changes");
        var change = changes.First();
        change.UserName.ShouldBe("Тест Operator");
        change.Summary.ShouldContain("20:00 → 18:00");
        change.Summary.ShouldContain("повторов");
        await EventuallyAsync(async () => (await DbAsync(db => db.SystemEvents.ToListAsync())).Any(e => e.Text.Contains("20:00 → 18:00")));
    }

    [Test]
    public async Task Viewer_CannotCreate_403()
    {
        var admin = await AdminAsync();
        var viewer = await ClientAsync(UserRole.Viewer);
        var sourceId = await ScheduleApi.SourceAsync(admin);

        (await viewer.PostJsonAsync("/api/schedules", ScheduleApi.Body(sourceId, await ScheduleApi.DefaultPresetAsync(admin))))
            .StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Test]
    public async Task Disable_ClearsNextRun()
    {
        var admin = await AdminAsync();
        var sourceId = await ScheduleApi.SourceAsync(admin);
        var schedule = await ScheduleApi.CreateAsync(admin, ScheduleApi.Body(sourceId, await ScheduleApi.DefaultPresetAsync(admin)));

        (await admin.PostJsonAsync($"/api/schedules/{schedule.Id}/enabled", new { isEnabled = false })).EnsureSuccessStatusCode();

        var loaded = await admin.GetJsonAsync<ScheduleView>($"/api/schedules/{schedule.Id}");
        loaded.IsEnabled.ShouldBeFalse();
        loaded.NextRunAt.ShouldBeNull();
    }

    [Test]
    public async Task Delete_KeepsRunsInHistory()
    {
        var admin = await AdminAsync();
        var sourceId = await ScheduleApi.SourceAsync(admin);
        var schedule = await ScheduleApi.CreateAsync(admin, ScheduleApi.Body(sourceId, await ScheduleApi.DefaultPresetAsync(admin)));
        var runId = (await (await admin.PostAsync($"/api/schedules/{schedule.Id}/run", null)).ReadJsonAsync<RunCreated>()).RunId;

        (await admin.DeleteAsync($"/api/schedules/{schedule.Id}")).EnsureSuccessStatusCode();

        var run = await DbAsync(db => db.Runs.SingleAsync(r => r.Id == runId));
        run.ScheduleId.ShouldBeNull();
        run.Name.ShouldBe("ЖК Север");
    }

    [Test]
    public async Task RunNow_CreatesRunTriggeredByUser()
    {
        var admin = await AdminAsync();
        var operatorClient = await ClientAsync(UserRole.Operator);
        var sourceId = await ScheduleApi.SourceAsync(admin);
        var schedule = await ScheduleApi.CreateAsync(admin, ScheduleApi.Body(sourceId, await ScheduleApi.DefaultPresetAsync(admin)));

        var response = await operatorClient.PostAsync($"/api/schedules/{schedule.Id}/run", null);

        response.EnsureSuccessStatusCode();
        var runId = (await response.ReadJsonAsync<RunCreated>()).RunId;
        var run = await DbAsync(db => db.Runs.SingleAsync(r => r.Id == runId));
        run.TriggeredByUserId.ShouldNotBeNull();
        run.ScheduleId.ShouldBe(schedule.Id);
    }
}
