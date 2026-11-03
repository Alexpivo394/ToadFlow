using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using ToadFlow.Contracts;
using ToadFlow.Server.Data.Entities;
using ToadFlow.Server.Queue;
using ToadFlow.Server.Tests.Infrastructure;

namespace ToadFlow.Server.Tests;

/// <summary>Номер попытки в протоколе агента: итог и ход чужой попытки игнорируются, агент без номера работает по-старому.</summary>
public class AttemptContractTests : ServerTest
{
    private async Task<FakeAgent> AgentAsync(string name = "FARM-01", params InstalledRevitDto[] revits)
    {
        var agent = await FakeAgent.RegisterAsync(Server, await AdminAsync(), name);
        await agent.ConnectAsync("1.8.0", revits.Length > 0 ? revits : [new InstalledRevitDto(2022, true)]);
        return agent;
    }

    private Task<Guid> SeedJobAsync(int maxAttempts = 2) => DbAsync(async db =>
    {
        var job = Seed.Job(db, Seed.Run(db).Id, maxAttempts: maxAttempts);
        await db.SaveChangesAsync();
        return job.Id;
    });

    private Task<Job> JobAsync(Guid id) => DbAsync(db => db.Jobs.AsNoTracking().SingleAsync(j => j.Id == id));

    private Task BumpAttemptAsync(Guid id, int attempt) =>
        DbAsync(db => db.Jobs.Where(j => j.Id == id).ExecuteUpdateAsync(j => j.SetProperty(x => x.Attempt, attempt)));

    [Test]
    public async Task Completion_StaleAttempt_Ignored()
    {
        await using var agent = await AgentAsync();
        var jobId = await SeedJobAsync();
        await agent.RequestJobAsync(1, 2022);
        await BumpAttemptAsync(jobId, 2);

        await agent.CompleteJobAsync(jobId, JobOutcome.Succeeded, attempt: 1);
        await agent.ReportProgressAsync(jobId, 1, 1, new JobLogDto(DateTimeOffset.UtcNow, "NWC", "чужая попытка"));

        var job = await JobAsync(jobId);
        job.Status.ShouldBe(JobStatus.Running);
        job.Attempt.ShouldBe(2);
        (await DbAsync(db => db.JobLogLines.CountAsync(l => l.JobId == jobId))).ShouldBe(0);

        await agent.ReportProgressAsync(jobId, 1, 2, new JobLogDto(DateTimeOffset.UtcNow, "NWC", "текущая попытка"));
        (await DbAsync(db => db.JobLogLines.CountAsync(l => l.JobId == jobId))).ShouldBe(1);
        await agent.CompleteJobAsync(jobId, JobOutcome.Succeeded, attempt: 2);
        (await JobAsync(jobId)).Status.ShouldBe(JobStatus.Succeeded);
    }

    [Test]
    public async Task Upload_StaleAttempt_409()
    {
        await using var agent = await AgentAsync();
        var (jobId, model) = await ClashFixtures.RunningJobAsync(Server, agent);
        await BumpAttemptAsync(jobId, 2);
        var files = ClashFixtures.ModelFiles(model.SnapshotKey);
        var client = ClashFixtures.AgentClient(Server, agent);

        (await client.PostJsonAsync("/api/agent/clash/uploads", files.Request(jobId, 1))).StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await client.PostJsonAsync("/api/agent/clash/uploads", files.Request(jobId, 2))).StatusCode.ShouldBe(HttpStatusCode.Created);
    }

    [Test]
    public async Task OldAgent_NoAttempt_CompletesAsBefore()
    {
        await using var agent = await AgentAsync();
        var jobId = await SeedJobAsync();
        await agent.RequestJobAsync(1, 2022);
        await BumpAttemptAsync(jobId, 2);

        await agent.ReportProgressAsync(jobId, 1, new JobLogDto(DateTimeOffset.UtcNow, "NWC", "ход"));
        await agent.CompleteJobAsync(jobId, JobOutcome.Succeeded);

        (await DbAsync(db => db.JobLogLines.CountAsync(l => l.JobId == jobId))).ShouldBe(1);
        (await JobAsync(jobId)).Status.ShouldBe(JobStatus.Succeeded);
    }

    [Test]
    public async Task Cancel_ClaimRace_ConditionalUpdate()
    {
        await using var agent = await AgentAsync();
        var admin = await AdminAsync();
        for (var i = 0; i < 8; i++)
        {
            var jobId = await SeedJobAsync();
            var claim = Task.Run(async () =>
            {
                await using var scope = Server.Services.CreateAsyncScope();
                return await scope.ServiceProvider.GetRequiredService<JobQueue>().ClaimAsync(agent.Id, new JobRequestDto(1, 2022, true));
            });
            var cancel = admin.PostAsync($"/api/jobs/{jobId}/cancel", null);
            await Task.WhenAll(claim, cancel);

            var job = await JobAsync(jobId);
            var claimed = (await claim)?.JobId == jobId;
            if (job.Status == JobStatus.Cancelled)
            {
                job.AgentId.ShouldBeNull();
                claimed.ShouldBeFalse();
            }
            else
            {
                job.Status.ShouldBe(JobStatus.Running);
                job.AgentId.ShouldBe(agent.Id);
                job.CancelRequested.ShouldBeTrue();
                await agent.CompleteJobAsync(jobId, JobOutcome.Cancelled);
            }
        }
    }

    [Test]
    public async Task Claim_InstanceLimit_Atomic()
    {
        await using var agent = await AgentAsync();
        for (var i = 0; i < 6; i++) await SeedJobAsync();
        var queue = Server.Services;

        var results = await Task.WhenAll(Enumerable.Range(0, 4).Select(slot => Task.Run(async () =>
        {
            await using var scope = queue.CreateAsyncScope();
            return await scope.ServiceProvider.GetRequiredService<JobQueue>().ClaimAsync(agent.Id, new JobRequestDto(slot + 1, 2022, true));
        })));

        results.Count(r => r is not null).ShouldBe(1);
        (await DbAsync(db => db.Jobs.CountAsync(j => j.Status == JobStatus.Running))).ShouldBe(1);
    }

    [Test]
    public async Task Hello_ReturnsActiveJobs()
    {
        await using var agent = await AgentAsync();
        var jobId = await SeedJobAsync();
        var other = await SeedJobAsync();
        var assignment = (await agent.RequestJobAsync(1, 2022)).ShouldNotBeNull();
        var running = assignment.JobId;

        var active = await agent.GetActiveJobsAsync();

        active.ShouldBe([new ActiveJobDto(running, 1)]);
        (running == jobId || running == other).ShouldBeTrue();
    }
}
