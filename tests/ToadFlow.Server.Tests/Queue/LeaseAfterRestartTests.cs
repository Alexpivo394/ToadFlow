using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Time.Testing;
using Shouldly;
using ToadFlow.Contracts;
using ToadFlow.Server.Data.Entities;
using ToadFlow.Server.Queue;
using ToadFlow.Server.Tests.Infrastructure;

namespace ToadFlow.Server.Tests;

/// <summary>Сервер, который простоял дольше аренды, не должен терять задания, которые агенты продолжают выполнять.</summary>
public class LeaseAfterRestartTests : ServerTest
{
    private readonly FakeTimeProvider _time = new(DateTimeOffset.UtcNow);

    public LeaseAfterRestartTests()
    {
        Time = _time;
        Services = services => services.Remove(services.Single(d => d.ServiceType == typeof(IHostedService) && d.ImplementationType == typeof(LeaseSweeper)));
    }

    private LeaseSweeper Sweeper() => ActivatorUtilities.CreateInstance<LeaseSweeper>(Server.Services);

    private Task<Job> JobAsync(Guid id) => DbAsync(db => db.Jobs.AsNoTracking().SingleAsync(j => j.Id == id));

    [Test]
    public async Task Restart_LongerThanLease_AgentResultAccepted()
    {
        await using var agent = await FakeAgent.RegisterAsync(Server, await AdminAsync());
        await agent.ConnectAsync(new InstalledRevitDto(2022, true));
        var jobId = await DbAsync(async db =>
        {
            var job = Seed.Job(db, Seed.Run(db).Id, maxAttempts: 2);
            await db.SaveChangesAsync();
            return job.Id;
        });
        var assignment = (await agent.RequestJobAsync(1, 2022)).ShouldNotBeNull();
        await DbAsync(db => db.Jobs.Where(j => j.Id == jobId).ExecuteUpdateAsync(j => j.SetProperty(x => x.LeaseUntil, _time.GetUtcNow().AddMinutes(-4))));

        var sweeper = Sweeper();
        await sweeper.ExtendLeasesAsync(CancellationToken.None);
        await sweeper.SweepAsync(CancellationToken.None);

        var job = await JobAsync(jobId);
        job.Status.ShouldBe(JobStatus.Running);
        job.Attempt.ShouldBe(1);
        job.LeaseUntil.ShouldNotBeNull().ShouldBe(_time.GetUtcNow() + LeaseOptions.StartupGrace, TimeSpan.FromSeconds(1));
        LeaseOptions.StartupGrace.ShouldBe(TimeSpan.FromMinutes(5));

        await agent.CompleteJobAsync(jobId, JobOutcome.Succeeded, attempt: assignment.Attempt);

        (await JobAsync(jobId)).Status.ShouldBe(JobStatus.Succeeded);
    }

    /// <summary>Задание, выданное уже после запуска сервера, продление запуска не трогает: его аренда свежая.</summary>
    [Test]
    public async Task ExtendLeases_JobIssuedAfterStart_Untouched()
    {
        await using var agent = await FakeAgent.RegisterAsync(Server, await AdminAsync());
        await agent.ConnectAsync(new InstalledRevitDto(2022, true));
        var jobId = await DbAsync(async db =>
        {
            var job = Seed.Job(db, Seed.Run(db).Id, maxAttempts: 2);
            await db.SaveChangesAsync();
            return job.Id;
        });
        (await agent.RequestJobAsync(1, 2022)).ShouldNotBeNull();
        var lease = (await JobAsync(jobId)).LeaseUntil.ShouldNotBeNull();

        await Sweeper().ExtendLeasesAsync(CancellationToken.None, _time.GetUtcNow() - TimeSpan.FromMinutes(1));

        (await JobAsync(jobId)).LeaseUntil.ShouldBe(lease);
    }

    [Test]
    public async Task Sweeper_CompleteRace_NoOverwrite()
    {
        await using var agent = await FakeAgent.RegisterAsync(Server, await AdminAsync());
        await agent.ConnectAsync(new InstalledRevitDto(2022, true));
        var jobId = await DbAsync(async db =>
        {
            var job = Seed.Job(db, Seed.Run(db).Id, maxAttempts: 2);
            await db.SaveChangesAsync();
            return job.Id;
        });
        await agent.RequestJobAsync(1, 2022);
        await DbAsync(db => db.Jobs.Where(j => j.Id == jobId).ExecuteUpdateAsync(j => j.SetProperty(x => x.LeaseUntil, _time.GetUtcNow().AddMinutes(-1))));
        // Sweeper уже прочитал задание как Running с истёкшей арендой...
        var stale = await JobAsync(jobId);
        stale.Status.ShouldBe(JobStatus.Running);

        // ...а итог успел прийти раньше его записи.
        await agent.CompleteJobAsync(jobId, JobOutcome.Succeeded);
        await using var scope = Server.Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<JobQueue>().FailInfrastructureAsync(stale, "агент потерял связь во время задания", ToadFlow.Server.Events.Actor.System);

        var job = await JobAsync(jobId);
        job.Status.ShouldBe(JobStatus.Succeeded);
        job.FinishedAt.ShouldNotBeNull();
        (await DbAsync(db => db.JobLogLines.CountAsync(l => l.JobId == jobId))).ShouldBe(0);
    }

    [Test]
    public async Task Sweeper_LeaseRenewedAfterRead_StaysRunning()
    {
        await using var agent = await FakeAgent.RegisterAsync(Server, await AdminAsync());
        await agent.ConnectAsync(new InstalledRevitDto(2022, true));
        var jobId = await DbAsync(async db =>
        {
            var job = Seed.Job(db, Seed.Run(db).Id, maxAttempts: 2);
            await db.SaveChangesAsync();
            return job.Id;
        });
        await agent.RequestJobAsync(1, 2022);
        await DbAsync(db => db.Jobs.Where(j => j.Id == jobId).ExecuteUpdateAsync(j => j.SetProperty(x => x.LeaseUntil, _time.GetUtcNow().AddMinutes(-1))));
        var stale = await JobAsync(jobId);

        // Heartbeat продлил аренду уже после того, как sweeper прочитал список истёкших.
        await agent.HeartbeatAsync(new InstanceStatusDto(1, 2022, 100, InstanceState.Busy, jobId, "NWC", _time.GetUtcNow()));
        await using var scope = Server.Services.CreateAsyncScope();
        var written = await scope.ServiceProvider.GetRequiredService<JobQueue>()
            .FailInfrastructureAsync(stale, "агент потерял связь во время задания", ToadFlow.Server.Events.Actor.System, _time.GetUtcNow());

        written.ShouldBeFalse();
        (await JobAsync(jobId)).Status.ShouldBe(JobStatus.Running);
    }
}
