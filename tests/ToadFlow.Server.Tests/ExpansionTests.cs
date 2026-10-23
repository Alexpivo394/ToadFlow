using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Time.Testing;
using Shouldly;
using ToadFlow.Contracts;
using ToadFlow.Contracts.Export;
using ToadFlow.Server.Data.Entities;
using ToadFlow.Server.Tests.Infrastructure;

namespace ToadFlow.Server.Tests;

public class ExpansionTests : ServerTest
{
    private async Task<(HttpClient Admin, ScheduleView Schedule)> ScheduleAsync(SourceKind kind = SourceKind.RevitServer)
    {
        var admin = await AdminAsync();
        var sourceId = await ScheduleApi.SourceAsync(admin, kind);
        var schedule = await ScheduleApi.CreateAsync(admin, ScheduleApi.Body(sourceId, await ScheduleApi.DefaultPresetAsync(admin),
            selection: new SelectionDto([], [new FolderSelectionDto("ЖК", true, true)]), overrides: new { purge = false }));
        return (admin, schedule);
    }

    private async Task<Run> RunNowAsync(HttpClient admin, Guid scheduleId)
    {
        var runId = (await (await admin.PostAsync($"/api/schedules/{scheduleId}/run", null)).ReadJsonAsync<RunCreated>()).RunId;
        await EventuallyAsync(async () => (await DbAsync(db => db.Runs.SingleAsync(r => r.Id == runId))).State != RunState.Expanding);
        return await DbAsync(db => db.Runs.SingleAsync(r => r.Id == runId));
    }

    private Task<List<Job>> JobsAsync(Guid runId) => DbAsync(db => db.Jobs.Where(j => j.RunId == runId).OrderBy(j => j.ModelName).ToListAsync());

    [Test]
    public async Task Expansion_UsesAgentAndCachesModels()
    {
        var (admin, schedule) = await ScheduleAsync();
        await using var agent = await FakeAgent.RegisterAsync(Server, admin);
        agent.ExpandSelection = (_, _) =>
        [
            new ExpandedModelDto("АР.rvt", "RSN://rs22/ЖК/АР.rvt", true, null, true, true),
            new ExpandedModelDto("КР.rvt", "RSN://rs22/ЖК/КР.rvt", true, null, true, false)
        ];
        await agent.ConnectAsync();

        var run = await RunNowAsync(admin, schedule.Id);

        run.State.ShouldBe(RunState.Queued);
        var jobs = await JobsAsync(run.Id);
        jobs.Count.ShouldBe(2);
        jobs[0].RequiresNwc.ShouldBeTrue();
        jobs[1].RequiresNwc.ShouldBeFalse();
        jobs.ShouldAllBe(j => j.MaxAttempts == 2 && j.TimeoutMinutes == 30 && j.Status == JobStatus.Queued);
        var payload = JsonSerializer.Deserialize<ExportJobPayload>(jobs[0].PayloadJson, TestJson.Options)!;
        payload.Settings.Purge.ShouldBeFalse();
        payload.OutputFolder.ShouldStartWith(@"\\nas\exports\ЖК Север\");
        payload.OutputFolder.ShouldNotContain("{");
        (await DbAsync(db => db.Schedules.SingleAsync(s => s.Id == schedule.Id))).LastKnownModels.ShouldNotBeNull().Count.ShouldBe(2);
    }

    [Test]
    public async Task Expansion_ServerSourceYearAppliedToModels()
    {
        var (admin, schedule) = await ScheduleAsync();
        await using var agent = await FakeAgent.RegisterAsync(Server, admin);
        agent.ExpandSelection = (_, _) => [new ExpandedModelDto("АР.rvt", "RSN://rs22/ЖК/АР.rvt", true, null, true, true)];
        await agent.ConnectAsync();

        var run = await RunNowAsync(admin, schedule.Id);

        (await JobsAsync(run.Id)).Single().RevitYear.ShouldBe(2022);
    }

    [Test]
    public async Task Expansion_UnknownYear_JobFailedImmediately()
    {
        var (admin, schedule) = await ScheduleAsync(SourceKind.Folder);
        await using var agent = await FakeAgent.RegisterAsync(Server, admin);
        agent.ExpandSelection = (_, _) =>
        [
            new ExpandedModelDto("АР.rvt", @"\\nas\projects\ЖК\АР.rvt", false, 2024, true, true),
            new ExpandedModelDto("битая.rvt", @"\\nas\projects\ЖК\битая.rvt", false, null, true, true)
        ];
        await agent.ConnectAsync();

        var run = await RunNowAsync(admin, schedule.Id);

        var jobs = await JobsAsync(run.Id);
        jobs.Single(j => j.ModelName == "АР.rvt").Status.ShouldBe(JobStatus.Queued);
        var broken = jobs.Single(j => j.ModelName == "битая.rvt");
        broken.Status.ShouldBe(JobStatus.Failed);
        broken.Note.ShouldNotBeNull().ShouldContain("версию Revit");
    }

    [Test]
    public async Task Expansion_NoAgentOnline_UsesCacheWithWarning()
    {
        var (admin, schedule) = await ScheduleAsync();
        await DbAsync(db => db.Schedules.Where(s => s.Id == schedule.Id).ExecuteUpdateAsync(s => s
            .SetProperty(x => x.LastKnownModels, new List<ExpandedModelDto> { new("АР.rvt", "RSN://rs22/ЖК/АР.rvt", true, null, true, true) })
            .SetProperty(x => x.LastKnownModelsAt, DateTimeOffset.UtcNow.AddDays(-1))));

        var run = await RunNowAsync(admin, schedule.Id);

        run.State.ShouldBe(RunState.Queued);
        run.Warning.ShouldNotBeNull().ShouldContain("нет в сети");
        (await JobsAsync(run.Id)).Count.ShouldBe(1);
    }

    [Test]
    public async Task Expansion_NoAgentNoCache_RunCompletesWithWarning()
    {
        var (admin, schedule) = await ScheduleAsync();

        var run = await RunNowAsync(admin, schedule.Id);

        run.State.ShouldBe(RunState.Completed);
        run.Warning.ShouldNotBeNull().ShouldContain("не удалось получить список моделей");
        run.FinishedAt.ShouldNotBeNull();
    }

    [Test]
    public async Task Expansion_DuplicateNames_RunWarning()
    {
        var (admin, schedule) = await ScheduleAsync();
        await using var agent = await FakeAgent.RegisterAsync(Server, admin);
        agent.ExpandSelection = (_, _) =>
        [
            new ExpandedModelDto("АР.rvt", "RSN://rs22/К1/АР.rvt", true, null, true, true),
            new ExpandedModelDto("АР.rvt", "RSN://rs22/К2/АР.rvt", true, null, true, true)
        ];
        await agent.ConnectAsync();

        var run = await RunNowAsync(admin, schedule.Id);

        run.Warning.ShouldNotBeNull().ShouldContain("АР.rvt");
    }
}

public class ScheduleRunnerTests : ServerTest
{
    private readonly FakeTimeProvider _time = new(DateTimeOffset.UtcNow);

    public ScheduleRunnerTests() => Time = _time;

    private async Task<ScheduleView> ScheduleAsync(RecurrenceKind kind = RecurrenceKind.Weekly, DateTimeOffset? onceAt = null)
    {
        var admin = await AdminAsync();
        var sourceId = await ScheduleApi.SourceAsync(admin);
        return await ScheduleApi.CreateAsync(admin, ScheduleApi.Body(sourceId, await ScheduleApi.DefaultPresetAsync(admin), kind: kind, onceAt: onceAt,
            mask: 0b1111111));
    }

    [Test]
    public async Task Runner_DueSchedule_StartsRunAndAdvances()
    {
        var schedule = await ScheduleAsync();
        var due = schedule.NextRunAt!.Value;

        _time.SetUtcNow(due.AddSeconds(31));

        await EventuallyAsync(async () => await DbAsync(db => db.Runs.AnyAsync(r => r.ScheduleId == schedule.Id)));
        var run = await DbAsync(db => db.Runs.SingleAsync(r => r.ScheduleId == schedule.Id));
        run.TriggeredByUserId.ShouldBeNull();
        await EventuallyAsync(async () => (await DbAsync(db => db.Schedules.SingleAsync(s => s.Id == schedule.Id))).NextRunAt > due);
    }

    [Test]
    public async Task Runner_MissedSeveralRuns_CatchesUpOnce()
    {
        var schedule = await ScheduleAsync();
        await DbAsync(db => db.Schedules.Where(s => s.Id == schedule.Id)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.NextRunAt, _time.GetUtcNow().AddDays(-3))));

        _time.Advance(TimeSpan.FromSeconds(31));

        await EventuallyAsync(async () => await DbAsync(db => db.Runs.AnyAsync(r => r.ScheduleId == schedule.Id)));
        _time.Advance(TimeSpan.FromSeconds(31));
        await Task.Delay(500);
        (await DbAsync(db => db.Runs.CountAsync(r => r.ScheduleId == schedule.Id))).ShouldBe(1);
        (await DbAsync(db => db.Schedules.SingleAsync(s => s.Id == schedule.Id))).NextRunAt.ShouldNotBeNull()
            .ShouldBeGreaterThan(_time.GetUtcNow());
    }

    [Test]
    public async Task Runner_OnceSchedule_DisabledAfterRun()
    {
        var schedule = await ScheduleAsync(RecurrenceKind.Once, _time.GetUtcNow().AddMinutes(5));

        _time.Advance(TimeSpan.FromMinutes(6));

        await EventuallyAsync(async () => await DbAsync(db => db.Runs.AnyAsync(r => r.ScheduleId == schedule.Id)));
        await EventuallyAsync(async () =>
        {
            var loaded = await DbAsync(db => db.Schedules.SingleAsync(s => s.Id == schedule.Id));
            return !loaded.IsEnabled && loaded.NextRunAt is null;
        });
    }
}

public class ExpansionFailureTests : ServerTest
{
    [Test]
    public async Task Expansion_UnexpectedError_RunCompletesInsteadOfHanging()
    {
        var admin = await AdminAsync();
        var sourceId = await ScheduleApi.SourceAsync(admin);
        var schedule = await ScheduleApi.CreateAsync(admin, ScheduleApi.Body(sourceId, await ScheduleApi.DefaultPresetAsync(admin)));
        await DbAsync(db => db.Schedules.Where(s => s.Id == schedule.Id)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.LastKnownModels, new List<ExpandedModelDto> { new("АР.rvt", "RSN://rs22/АР.rvt", true, null, true, true) })
                .SetProperty(x => x.ModuleKind, "removed-module")));

        var runId = (await (await admin.PostAsync($"/api/schedules/{schedule.Id}/run", null)).ReadJsonAsync<RunCreated>()).RunId;

        await EventuallyAsync(async () => (await DbAsync(db => db.Runs.SingleAsync(r => r.Id == runId))).State == RunState.Completed);
        (await DbAsync(db => db.Runs.SingleAsync(r => r.Id == runId))).Warning.ShouldNotBeNull().ShouldContain("Не удалось построить задания");
    }
}
