using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Time.Testing;
using Shouldly;
using ToadFlow.Contracts;
using ToadFlow.Server.Data.Entities;
using ToadFlow.Server.Tests.Infrastructure;

namespace ToadFlow.Server.Tests;

public class QueueTests : ServerTest
{
    private readonly FakeTimeProvider _time = new(DateTimeOffset.UtcNow);

    public QueueTests() => Time = _time;

    private async Task<FakeAgent> AgentAsync(string name = "FARM-01", params InstalledRevitDto[] revits)
    {
        var agent = await FakeAgent.RegisterAsync(Server, await AdminAsync(), name);
        await agent.ConnectAsync(revits.Length > 0 ? revits : [new InstalledRevitDto(2022, true), new InstalledRevitDto(2024, false)]);
        return agent;
    }

    private Task<Guid> SeedJobAsync(int year = 2022, bool requiresNwc = false, int priority = 0, int maxAttempts = 1, DateTimeOffset? createdAt = null) =>
        DbAsync(async db =>
        {
            var run = Seed.Run(db);
            var job = Seed.Job(db, run.Id, year, requiresNwc, priority, maxAttempts, createdAt);
            await db.SaveChangesAsync();
            return job.Id;
        });

    private Task<Job> JobAsync(Guid id) => DbAsync(db => db.Jobs.AsNoTracking().SingleAsync(j => j.Id == id));

    [Test]
    public async Task Claim_MatchesYearOnly()
    {
        await using var agent = await AgentAsync();
        var jobId = await SeedJobAsync(2022);

        (await agent.RequestJobAsync(1, 2024)).ShouldBeNull();
        var assignment = (await agent.RequestJobAsync(1, 2022)).ShouldNotBeNull();

        assignment.JobId.ShouldBe(jobId);
        assignment.Attempt.ShouldBe(1);
        assignment.TimeoutMinutes.ShouldBe(30);
        var job = await JobAsync(jobId);
        job.Status.ShouldBe(JobStatus.Running);
        job.AgentId.ShouldBe(agent.Id);
        job.InstanceSlot.ShouldBe(1);
        job.LeaseUntil.ShouldNotBeNull();
    }

    [Test]
    public async Task Claim_AgentBeforeModules_GetsOnlyExport()
    {
        await using var old = await FakeAgent.RegisterAsync(Server, await AdminAsync(), "FARM-OLD");
        await old.ConnectAsync("1.0.3", new InstalledRevitDto(2022, true));
        await using var fresh = await AgentAsync("FARM-NEW", new InstalledRevitDto(2022, true));
        var jobId = await SeedJobAsync();
        await DbAsync(db => db.Jobs.Where(j => j.Id == jobId).ExecuteUpdateAsync(j => j.SetProperty(x => x.ModuleKind, ModuleKinds.Loi)));

        // Аддин агента 1.0.x принял бы проверку за выгрузку без форматов и отчитался бы «успешно», ничего не сделав.
        (await old.RequestJobAsync(1, 2022)).ShouldBeNull();
        (await fresh.RequestJobAsync(1, 2022)).ShouldNotBeNull().JobId.ShouldBe(jobId);
    }

    [Test]
    [Arguments("1.0.3", false)]
    [Arguments("1.0.0", false)]
    [Arguments("1.1.0", true)]
    [Arguments("2.0.0-beta.1", true)]
    [Arguments("0.1.0", true)]
    [Arguments("", true)]
    public void AgentVersion_KnowsModuleKind(string version, bool knows) =>
        Agents.AgentCapabilities.KnowsModuleKind(version).ShouldBe(knows);

    [Test]
    public async Task Claim_NwcJobNeedsExporter()
    {
        await using var agent = await AgentAsync();
        await SeedJobAsync(2024, requiresNwc: true);

        (await agent.RequestJobAsync(1, 2024, hasNwcExporter: false)).ShouldBeNull();
        (await agent.RequestJobAsync(1, 2024, hasNwcExporter: true)).ShouldNotBeNull();
    }

    [Test]
    public async Task Claim_HigherPriorityFirst_ThenOldest()
    {
        var now = DateTimeOffset.UtcNow;
        await using var agent = await AgentAsync(revits: [new InstalledRevitDto(2022, true)]);
        await DbAsync(db => db.Agents.Where(a => a.Id == agent.Id)
            .ExecuteUpdateAsync(a => a.SetProperty(x => x.Limits, new List<VersionLimitDto> { new(2022, 3) })));
        var older = await SeedJobAsync(createdAt: now.AddMinutes(-10));
        var newer = await SeedJobAsync(createdAt: now.AddMinutes(-5));
        var urgent = await SeedJobAsync(priority: 1, createdAt: now);

        (await agent.RequestJobAsync(1, 2022))!.JobId.ShouldBe(urgent);
        (await agent.RequestJobAsync(2, 2022))!.JobId.ShouldBe(older);
        (await agent.RequestJobAsync(3, 2022))!.JobId.ShouldBe(newer);
    }

    [Test]
    public async Task Queue_ConcurrentRequests_EachJobOnce()
    {
        var jobs = new List<Guid>();
        for (var i = 0; i < 20; i++) jobs.Add(await SeedJobAsync());
        var agents = new List<FakeAgent>();
        for (var i = 0; i < 4; i++)
        {
            var agent = await AgentAsync($"FARM-0{i}", new InstalledRevitDto(2022, true));
            await DbAsync(db => db.Agents.Where(a => a.Id == agent.Id)
                .ExecuteUpdateAsync(a => a.SetProperty(x => x.Limits, new List<VersionLimitDto> { new(2022, 3) })));
            agents.Add(agent);
        }

        // Общий лимит экземпляров — забота агента; здесь проверяется только атомарность выдачи, поэтому лимит снят через «завершение».
        var claimed = await Task.WhenAll(agents.SelectMany(agent => Enumerable.Range(0, 10).Select(async slot =>
        {
            var assignment = await agent.RequestJobAsync(slot, 2022);
            if (assignment is not null) await agent.CompleteJobAsync(assignment.JobId, JobOutcome.Succeeded);
            return assignment?.JobId;
        })));

        var ids = claimed.Where(id => id is not null).Select(id => id!.Value).ToList();
        ids.Count.ShouldBe(ids.Distinct().Count());
        foreach (var agent in agents) await agent.DisposeAsync();
    }

    [Test]
    public async Task Claim_DrainingAgent_GetsNothing()
    {
        var admin = await AdminAsync();
        await using var agent = await AgentAsync();
        await SeedJobAsync();

        (await admin.PostJsonAsync($"/api/agents/{agent.Id}/drain", new { draining = true })).EnsureSuccessStatusCode();

        (await agent.RequestJobAsync(1, 2022)).ShouldBeNull();
    }

    [Test]
    public async Task Claim_RespectsMaxInstancesPerYear()
    {
        await using var agent = await AgentAsync(); // по умолчанию 2022: 1
        await SeedJobAsync();
        await SeedJobAsync();

        (await agent.RequestJobAsync(1, 2022)).ShouldNotBeNull();
        (await agent.RequestJobAsync(2, 2022)).ShouldBeNull();
    }

    [Test]
    public async Task Complete_InfrastructureFailure_RequeuesUntilAttemptsExhausted()
    {
        await using var agent = await AgentAsync();
        var jobId = await SeedJobAsync(maxAttempts: 2);

        await agent.RequestJobAsync(1, 2022);
        await agent.CompleteJobAsync(jobId, JobOutcome.InfrastructureFailure, "Revit не отвечал, убит по таймауту");
        var afterFirst = await JobAsync(jobId);
        afterFirst.Status.ShouldBe(JobStatus.Queued);
        afterFirst.AgentId.ShouldBeNull();

        (await agent.RequestJobAsync(1, 2022))!.Attempt.ShouldBe(2);
        await agent.CompleteJobAsync(jobId, JobOutcome.InfrastructureFailure, "Revit упал");

        var final = await JobAsync(jobId);
        final.Status.ShouldBe(JobStatus.Failed);
        final.Note.ShouldNotBeNull().ShouldContain("Revit упал");
        var log = await DbAsync(db => db.JobLogLines.Where(l => l.JobId == jobId).Select(l => l.Text).ToListAsync());
        log.ShouldContain(text => text.Contains("попытка 1 из 2"));
    }

    [Test]
    public async Task Complete_ModelFailure_NotRetried()
    {
        await using var agent = await AgentAsync();
        var jobId = await SeedJobAsync(maxAttempts: 3);

        await agent.RequestJobAsync(1, 2022);
        await agent.CompleteJobAsync(jobId, JobOutcome.Failed, "NWC: вид «Navisworks» не найден", """{"rvt":"Done"}""");

        var job = await JobAsync(jobId);
        job.Status.ShouldBe(JobStatus.Failed);
        job.ResultJson.ShouldNotBeNull();
        job.FinishedAt.ShouldNotBeNull();
    }

    [Test]
    public async Task Complete_ForeignJob_Ignored()
    {
        await using var owner = await AgentAsync("FARM-01");
        await using var stranger = await AgentAsync("FARM-02");
        var jobId = await SeedJobAsync();

        await owner.RequestJobAsync(1, 2022);
        await stranger.CompleteJobAsync(jobId, JobOutcome.Succeeded);

        (await JobAsync(jobId)).Status.ShouldBe(JobStatus.Running);
    }

    [Test]
    public async Task Progress_AppendsLog()
    {
        await using var agent = await AgentAsync();
        var jobId = await SeedJobAsync();
        await agent.RequestJobAsync(1, 2022);

        await agent.ReportProgressAsync(jobId, 1, new JobLogDto(DateTimeOffset.UtcNow, "Открытие", "открытие модели"),
            new JobLogDto(DateTimeOffset.UtcNow, "NWC", "экспорт NWC"));

        var lines = await DbAsync(db => db.JobLogLines.Where(l => l.JobId == jobId).OrderBy(l => l.Id).ToListAsync());
        lines.Select(l => l.Stage).ShouldBe(["Открытие", "NWC"]);
    }

    [Test]
    public async Task LeaseSweeper_ExpiredLease_Requeues()
    {
        await using var agent = await AgentAsync();
        var jobId = await SeedJobAsync(maxAttempts: 2);
        await agent.RequestJobAsync(1, 2022);

        _time.Advance(TimeSpan.FromMinutes(3));

        await EventuallyAsync(async () => (await JobAsync(jobId)).Status == JobStatus.Queued);
    }

    [Test]
    public async Task LeaseSweeper_ExpiredLease_LastAttempt_Fails()
    {
        await using var agent = await AgentAsync();
        var jobId = await SeedJobAsync(maxAttempts: 1);
        await agent.RequestJobAsync(1, 2022);

        _time.Advance(TimeSpan.FromMinutes(3));

        await EventuallyAsync(async () => (await JobAsync(jobId)).Status == JobStatus.Failed);
        (await JobAsync(jobId)).Note.ShouldNotBeNull().ShouldContain("потерял связь");
    }

    [Test]
    public async Task LeaseSweeper_CancelRequested_CancelsInsteadOfRequeue()
    {
        await using var agent = await AgentAsync();
        var jobId = await SeedJobAsync(maxAttempts: 2);
        await agent.RequestJobAsync(1, 2022);
        await DbAsync(db => db.Jobs.Where(j => j.Id == jobId).ExecuteUpdateAsync(j => j.SetProperty(x => x.CancelRequested, true)));

        _time.Advance(TimeSpan.FromMinutes(3));

        // В очередь отменённое не вернуть: оттуда оно не выдаётся, и запуск висел бы вечно.
        await EventuallyAsync(async () => (await JobAsync(jobId)).Status == JobStatus.Cancelled);
        var run = await DbAsync(async db => await db.Runs.SingleAsync(r => r.Id == db.Jobs.Single(j => j.Id == jobId).RunId));
        run.State.ShouldBe(RunState.Cancelled);
    }

    [Test]
    public async Task Heartbeat_RenewsLease()
    {
        await using var agent = await AgentAsync();
        var jobId = await SeedJobAsync();
        await agent.RequestJobAsync(1, 2022);
        var before = (await JobAsync(jobId)).LeaseUntil!.Value;

        _time.Advance(TimeSpan.FromSeconds(30));
        await agent.HeartbeatAsync(new InstanceStatusDto(1, 2022, 100, InstanceState.Busy, jobId, "NWC", _time.GetUtcNow()));

        (await JobAsync(jobId)).LeaseUntil!.Value.ShouldBe(before.AddSeconds(30), TimeSpan.FromSeconds(1));
    }

    [Test]
    public async Task Run_CompletesWhenAllJobsFinal()
    {
        await using var agent = await AgentAsync();
        var (runId, first, second) = await DbAsync(async db =>
        {
            var run = Seed.Run(db);
            var a = Seed.Job(db, run.Id);
            var b = Seed.Job(db, run.Id, name: "КР.rvt");
            await db.SaveChangesAsync();
            return (run.Id, a.Id, b.Id);
        });
        await DbAsync(db => db.Agents.Where(a => a.Id == agent.Id)
            .ExecuteUpdateAsync(a => a.SetProperty(x => x.Limits, new List<VersionLimitDto> { new(2022, 2) })));

        await agent.RequestJobAsync(1, 2022);
        (await DbAsync(db => db.Runs.SingleAsync(r => r.Id == runId))).State.ShouldBe(RunState.Running);
        await agent.RequestJobAsync(2, 2022);
        await agent.CompleteJobAsync(first, JobOutcome.Succeeded);
        await agent.CompleteJobAsync(second, JobOutcome.Warning);

        var run = await DbAsync(db => db.Runs.SingleAsync(r => r.Id == runId));
        run.State.ShouldBe(RunState.Completed);
        run.FinishedAt.ShouldNotBeNull();
    }
}

public class AgentInstanceViewTests : ServerTest
{
    private sealed record InstanceRow(int Slot, Guid? JobId, string? ModelName);

    private sealed record AgentRow(Guid Id, InstanceRow[] Instances);

    [Test]
    public async Task AgentList_BusyInstanceShowsModelName()
    {
        var admin = await AdminAsync();
        await using var agent = await FakeAgent.RegisterAsync(Server, admin);
        await agent.ConnectAsync(new InstalledRevitDto(2022, true));
        await DbAsync(async db =>
        {
            var run = Seed.Run(db);
            Seed.Job(db, run.Id, name: "ЖК-Север_АР_К1.rvt");
            await db.SaveChangesAsync();
        });
        var jobId = (await agent.RequestJobAsync(1, 2022))!.JobId;

        await agent.HeartbeatAsync(new InstanceStatusDto(1, 2022, 100, InstanceState.Busy, jobId, "Экспорт NWC", DateTimeOffset.UtcNow));

        var instance = (await admin.GetJsonAsync<AgentRow[]>("/api/agents")).Single().Instances.ShouldHaveSingleItem();
        instance.ModelName.ShouldBe("ЖК-Север_АР_К1.rvt");
    }
}
