using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using ToadFlow.Contracts;
using ToadFlow.Contracts.Clash;
using ToadFlow.Server.Clash;
using ToadFlow.Server.Clash.Engine;
using ToadFlow.Server.Clash.Parts;
using ToadFlow.Server.Data;
using ToadFlow.Server.Data.Entities;
using ToadFlow.Server.Modules;
using ToadFlow.Server.Tests.Infrastructure;
using static ToadFlow.Server.Tests.Infrastructure.ClashDraftFixtures;

namespace ToadFlow.Server.Tests;

/// <summary>Утверждённые части в расписании выгрузки: после всех заданий запуска — прогон каждой части.</summary>
public class ClashApprovedScheduleTests : ServerTest
{
    private async Task<(HttpClient Admin, FakeAgent Agent, Guid PartId, Guid[] Models)> ApprovedPartAsync(bool approve = true)
    {
        var admin = await AdminAsync();
        var agent = await FakeAgent.RegisterAsync(Server, admin);
        await agent.ConnectAsync("1.8.0", new InstalledRevitDto(2022, true));
        var partId = await PartAsync(Server, admin, agent, "POL_K1_AR.rvt", "POL_K1_OV.rvt");
        if (approve) (await admin.PostJsonAsync($"/api/clash/parts/{partId}/approve", new { comment = (string?)null, run = false })).EnsureSuccessStatusCode();
        var models = (await admin.GetJsonAsync<ClashPartView>($"/api/clash/parts/{partId}")).Models.Select(m => m.ModelId).ToArray();
        return (admin, agent, partId, models);
    }

    /// <summary>Запуск расписания выгрузки с заданием на каждую модель части; статусы заданий — по порядку моделей.</summary>
    private Task<(Guid RunId, List<Job> Jobs)> ScheduleRunAsync(Guid partId, Guid[] models, bool cancelled, params JobStatus[] statuses) => DbAsync(async db =>
    {
        var source = Seed.Source(db);
        var schedule = Seed.Schedule(db, source.Id);
        schedule.ModuleKind = ModuleKinds.ClashExtract;
        schedule.ModuleSettingsJson = JsonSerializer.Serialize(new ClashScheduleSettings("Navisworks", "", [partId]), ToadFlowDb.Json);
        var run = Seed.Run(db, RunState.Running, schedule.Id);
        run.CancelRequested = cancelled;
        var jobs = models.Select((model, i) =>
        {
            var job = Seed.Job(db, run.Id, name: $"m{i}.rvt");
            job.ModuleKind = ModuleKinds.ClashExtract;
            job.Status = statuses[i];
            job.PayloadJson = JsonSerializer.Serialize(new ClashExtractPayload($"m{i}.rvt", "", true, 2022, model.ToString("N"), "Navisworks", ""), ToadFlowDb.Json);
            return job;
        }).ToList();
        await db.SaveChangesAsync();
        return (run.Id, jobs);
    });

    private async Task CompletedAsync(Job job)
    {
        using var scope = Server.Services.CreateScope();
        var module = scope.ServiceProvider.GetServices<IServerModule>().Single(m => m.Kind == ModuleKinds.ClashExtract);
        await module.OnJobCompletedAsync(job, scope.ServiceProvider, CancellationToken.None);
    }

    private Task<List<EngineRun>> ApprovedRunsAsync(Guid partId) =>
        DbAsync(db => db.Set<EngineRun>().AsNoTracking().Where(r => r.PartId == partId && r.Kind == EngineRunKind.Approved).ToListAsync());

    [Test]
    public async Task Schedule_AllJobsDone_QueuesApprovedRunPerPart()
    {
        var (_, agent, partId, models) = await ApprovedPartAsync();
        await using var _ = agent;
        var (runId, jobs) = await ScheduleRunAsync(partId, models, false, JobStatus.Succeeded, JobStatus.Running);

        await CompletedAsync(jobs[0]);
        (await ApprovedRunsAsync(partId)).ShouldBeEmpty(); // второе задание ещё идёт

        await DbAsync(db => db.Jobs.Where(j => j.Id == jobs[1].Id).ExecuteUpdateAsync(j => j.SetProperty(x => x.Status, JobStatus.Warning)));
        await CompletedAsync(jobs[1]);

        var run = (await ApprovedRunsAsync(partId)).ShouldHaveSingleItem();
        (run.TriggerRunId, run.Version, run.State).ShouldBe((runId, (int?)1, EngineRunState.Queued));
        run.UncheckedModels.ShouldBeEmpty();
    }

    [Test]
    public async Task Schedule_JobsFinishTogether_OneApprovedRun()
    {
        var (_, agent, partId, models) = await ApprovedPartAsync();
        await using var _ = agent;
        var (_, jobs) = await ScheduleRunAsync(partId, models, false, JobStatus.Succeeded, JobStatus.Succeeded);

        await Task.WhenAll(CompletedAsync(jobs[0]), CompletedAsync(jobs[1]), CompletedAsync(jobs[0]));

        (await ApprovedRunsAsync(partId)).ShouldHaveSingleItem();
    }

    [Test]
    public async Task Schedule_SameTriggerAfterClaim_NoSecondRun()
    {
        var (_, agent, partId, models) = await ApprovedPartAsync();
        await using var _ = agent;
        var (_, jobs) = await ScheduleRunAsync(partId, models, false, JobStatus.Succeeded, JobStatus.Succeeded);
        await CompletedAsync(jobs[0]);
        (await EngineClient(Server).PostAsync("/api/engine/runs/claim", null)).StatusCode.ShouldBe(HttpStatusCode.OK);

        await CompletedAsync(jobs[1]);

        (await ApprovedRunsAsync(partId)).ShouldHaveSingleItem();
    }

    [Test]
    public async Task Schedule_FailedJob_ModelUnchecked()
    {
        var (_, agent, partId, models) = await ApprovedPartAsync();
        await using var _ = agent;
        var (_, jobs) = await ScheduleRunAsync(partId, models, false, JobStatus.Succeeded, JobStatus.Failed);

        await CompletedAsync(jobs[1]);

        (await ApprovedRunsAsync(partId)).ShouldHaveSingleItem().UncheckedModels.ShouldBe([models[1]]);
    }

    [Test]
    public async Task Schedule_CancelledRun_NoApprovedRun()
    {
        var (_, agent, partId, models) = await ApprovedPartAsync();
        await using var _ = agent;
        var (_, jobs) = await ScheduleRunAsync(partId, models, true, JobStatus.Succeeded, JobStatus.Cancelled);

        await CompletedAsync(jobs[1]);

        (await ApprovedRunsAsync(partId)).ShouldBeEmpty();
    }

    [Test]
    public async Task Schedule_PartNotApproved_ValidationError()
    {
        var (admin, agent, partId, _) = await ApprovedPartAsync(approve: false);
        await using var _ = agent;
        var sourceId = await ScheduleApi.SourceAsync(admin);

        var notApproved = await admin.PostJsonAsync("/api/schedules", Body(sourceId, new { viewName = "Navisworks", parts = new[] { partId } }));
        var unknown = await admin.PostJsonAsync("/api/schedules", Body(sourceId, new { viewName = "Navisworks", parts = new[] { Guid.NewGuid() } }));

        notApproved.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await notApproved.Content.ReadAsStringAsync()).ShouldContain("не утверждена");
        unknown.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await unknown.Content.ReadAsStringAsync()).ShouldContain("Часть не найдена");
    }

    [Test]
    public async Task Schedule_OldSettingsWithoutParts_Valid()
    {
        var admin = await AdminAsync();
        var sourceId = await ScheduleApi.SourceAsync(admin);

        (await admin.PostJsonAsync("/api/schedules", Body(sourceId, new { viewName = "Navisworks" }))).StatusCode.ShouldBe(HttpStatusCode.Created);
        ClashScheduleSettings.TryParse(JsonDocument.Parse("""{"viewName":"Navisworks"}""").RootElement).ShouldNotBeNull().Parts.ShouldBeEmpty();
    }

    private static object Body(Guid sourceId, object moduleSettings) => new
    {
        name = "Коллизии ЖК Север",
        moduleKind = ModuleKinds.ClashExtract,
        sourceId,
        outputFolderTemplate = "",
        selection = new SelectionDto([new ModelSelectionDto("АР.rvt", "RSN://rs22/ЖК/АР.rvt", true, false)], []),
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
}
