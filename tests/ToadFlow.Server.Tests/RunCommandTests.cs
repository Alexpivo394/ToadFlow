using System.Net;
using Microsoft.EntityFrameworkCore;
using Shouldly;
using ToadFlow.Contracts;
using ToadFlow.Server.Data.Entities;
using ToadFlow.Server.Tests.Infrastructure;

namespace ToadFlow.Server.Tests;

public class RunCommandTests : ServerTest
{
    private async Task<FakeAgent> AgentAsync(HttpClient admin)
    {
        var agent = await FakeAgent.RegisterAsync(Server, admin);
        await agent.ConnectAsync(new InstalledRevitDto(2022, true));
        return agent;
    }

    /// <summary>Запуск с тремя заданиями: одно взял агент, два в очереди.</summary>
    private async Task<(Guid RunId, Guid Running, Guid[] Queued)> RunWithJobsAsync(FakeAgent agent)
    {
        var (runId, jobs) = await DbAsync(async db =>
        {
            var run = Seed.Run(db);
            var created = new[] { Seed.Job(db, run.Id, name: "А.rvt"), Seed.Job(db, run.Id, name: "Б.rvt"), Seed.Job(db, run.Id, name: "В.rvt") };
            await db.SaveChangesAsync();
            return (run.Id, created.Select(j => j.Id).ToArray());
        });
        var running = (await agent.RequestJobAsync(1, 2022))!.JobId;
        return (runId, running, jobs.Where(id => id != running).ToArray());
    }

    private Task<Job> JobAsync(Guid id) => DbAsync(db => db.Jobs.AsNoTracking().SingleAsync(j => j.Id == id));

    [Test]
    public async Task CancelRun_QueuedJobsCancelled_RunningSignalled()
    {
        var admin = await AdminAsync();
        var operatorClient = await ClientAsync(UserRole.Operator);
        await using var agent = await AgentAsync(admin);
        var (runId, running, queued) = await RunWithJobsAsync(agent);

        (await operatorClient.PostAsync($"/api/runs/{runId}/cancel", null)).EnsureSuccessStatusCode();

        foreach (var id in queued) (await JobAsync(id)).Status.ShouldBe(JobStatus.Cancelled);
        (await JobAsync(running)).CancelRequested.ShouldBeTrue();
        await EventuallyAsync(() => Task.FromResult(agent.Cancelled.Contains(running)));

        await agent.CompleteJobAsync(running, JobOutcome.Cancelled);
        await EventuallyAsync(async () => (await DbAsync(db => db.Runs.SingleAsync(r => r.Id == runId))).State == RunState.Cancelled);
    }

    [Test]
    public async Task CancelJob_Single()
    {
        var admin = await AdminAsync();
        await using var agent = await AgentAsync(admin);
        var (_, _, queued) = await RunWithJobsAsync(agent);

        (await admin.PostAsync($"/api/jobs/{queued[0]}/cancel", null)).EnsureSuccessStatusCode();

        (await JobAsync(queued[0])).Status.ShouldBe(JobStatus.Cancelled);
        (await JobAsync(queued[1])).Status.ShouldBe(JobStatus.Queued);
    }

    [Test]
    public async Task CancelJob_Finished_409()
    {
        var admin = await AdminAsync();
        await using var agent = await AgentAsync(admin);
        var (_, running, _) = await RunWithJobsAsync(agent);
        await agent.CompleteJobAsync(running, JobOutcome.Succeeded);

        (await admin.PostAsync($"/api/jobs/{running}/cancel", null)).StatusCode.ShouldBe(HttpStatusCode.Conflict);
    }

    [Test]
    public async Task RetryFailed_CreatesNewRunWithFailedOnly()
    {
        var admin = await AdminAsync();
        await using var agent = await AgentAsync(admin);
        var (runId, running, queued) = await RunWithJobsAsync(agent);
        await agent.CompleteJobAsync(running, JobOutcome.Failed, "NWC: ошибка");

        var response = await admin.PostAsync($"/api/runs/{runId}/retry-failed", null);

        response.EnsureSuccessStatusCode();
        var newRunId = (await response.ReadJsonAsync<RunCreated>()).RunId;
        var newRun = await DbAsync(db => db.Runs.SingleAsync(r => r.Id == newRunId));
        newRun.Name.ShouldContain("повтор упавших");
        newRun.TriggeredByUserId.ShouldNotBeNull();
        var copy = (await DbAsync(db => db.Jobs.Where(j => j.RunId == newRunId).ToListAsync())).ShouldHaveSingleItem();
        copy.ModelName.ShouldBe((await JobAsync(running)).ModelName);
        copy.Status.ShouldBe(JobStatus.Queued);
        copy.Attempt.ShouldBe(0);
    }

    [Test]
    public async Task RetryFailed_NoFailed_409()
    {
        var admin = await AdminAsync();
        await using var agent = await AgentAsync(admin);
        var (runId, _, _) = await RunWithJobsAsync(agent);

        (await admin.PostAsync($"/api/runs/{runId}/retry-failed", null)).StatusCode.ShouldBe(HttpStatusCode.Conflict);
    }

    [Test]
    public async Task RetryJob_CreatesRunWithOneJob()
    {
        var admin = await AdminAsync();
        await using var agent = await AgentAsync(admin);
        var (_, running, _) = await RunWithJobsAsync(agent);
        await agent.CompleteJobAsync(running, JobOutcome.Warning);

        var newRunId = (await (await admin.PostAsync($"/api/jobs/{running}/retry", null)).ReadJsonAsync<RunCreated>()).RunId;

        (await DbAsync(db => db.Jobs.CountAsync(j => j.RunId == newRunId))).ShouldBe(1);
    }

    [Test]
    public async Task Kill_ForwardsToAgentAndLogsEvent()
    {
        var admin = await AdminAsync();
        var operatorClient = await ClientAsync(UserRole.Operator);
        await using var agent = await AgentAsync(admin);

        (await operatorClient.PostAsync($"/api/agents/{agent.Id}/instances/2/kill", null)).EnsureSuccessStatusCode();

        await EventuallyAsync(() => Task.FromResult(agent.Killed.Contains(2)));
        await EventuallyAsync(async () =>
            (await DbAsync(db => db.SystemEvents.ToListAsync())).Any(e => e.Text.Contains("Вручную убит Revit #2") && e.ActorName == "Тест Operator"));
    }

    [Test]
    public async Task Kill_AgentOffline_409()
    {
        var admin = await AdminAsync();
        var agent = await AgentAsync(admin);
        await agent.DisposeAsync();
        await EventuallyAsync(async () => (await DbAsync(db => db.Agents.SingleAsync())).State == AgentState.Offline);

        (await admin.PostAsync($"/api/agents/{agent.Id}/instances/1/kill", null)).StatusCode.ShouldBe(HttpStatusCode.Conflict);
    }

    [Test]
    public async Task Tree_ProxiesToAgent()
    {
        var admin = await AdminAsync();
        await using var agent = await AgentAsync(admin);
        agent.ListChildren = (_, path) => [new SourceEntryDto("Корпус1", path + "/Корпус1", "", true), new SourceEntryDto("АР.rvt", path, "RSN://rs22/ЖК/АР.rvt", false)];
        var sourceId = await ScheduleApi.SourceAsync(admin);

        var entries = await admin.GetJsonAsync<SourceEntryDto[]>($"/api/sources/{sourceId}/tree?path=ЖК");

        entries.Length.ShouldBe(2);
        entries[0].FolderPath.ShouldBe("ЖК/Корпус1");
    }

    [Test]
    public async Task Tree_NoAgent_503()
    {
        var admin = await AdminAsync();
        var sourceId = await ScheduleApi.SourceAsync(admin);

        var response = await admin.GetAsync($"/api/sources/{sourceId}/tree?path=");

        response.StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);
        (await response.Content.ReadAsStringAsync()).ShouldContain("нет агентов");
    }
}
