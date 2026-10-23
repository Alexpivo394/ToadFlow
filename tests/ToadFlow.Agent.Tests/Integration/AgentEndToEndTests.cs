using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using ToadFlow.Agent.Tests.Infrastructure;
using ToadFlow.Contracts;
using ToadFlow.Server.Agents;
using ToadFlow.Server.Tests.Infrastructure;

namespace ToadFlow.Agent.Tests.Integration;

public class AgentEndToEndTests : ServerTest
{
    private sealed record IdBody(Guid Id);

    private sealed record JobView(Guid Id, string ModelName, JobStatus Status, int Attempt, string? Note, JsonElement? Result, int? RevitYear);

    private sealed record RunView(Guid Id, RunState State);

    private sealed record Details(RunView Run, JobView[] Jobs);

    private sealed record InstanceView(int Slot, InstanceState State, string? ModelName);

    private sealed record AgentRow(Guid Id, AgentState State, InstanceView[] Instances);

    private sealed record LogLine(string Stage, string Text);

    private readonly ModelFolder _models = new();
    private readonly FakeLauncher _launcher = new();
    private AgentHarness? _agent;
    private HttpClient _admin = null!;

    [After(Test)]
    public async Task CleanupAsync()
    {
        if (_agent is not null) await _agent.DisposeAsync();
        _models.Dispose();
    }

    private async Task<AgentHarness> AgentAsync(TimeSpan? silence = null)
    {
        _admin = await AdminAsync();
        _agent = await AgentHarness.StartAsync(Server, _admin, _launcher, silence);
        await EventuallyAsync(async () => (await _admin.GetJsonAsync<AgentRow[]>("/api/agents")).Any(a => a.State == AgentState.Online));
        return _agent;
    }

    private async Task<Guid> FolderSourceAsync()
    {
        var response = await _admin.PostJsonAsync("/api/sources", new { name = "Папка", kind = SourceKind.Folder, host = _models.Root, revitYear = (int?)null, rootPath = "" });
        response.EnsureSuccessStatusCode();
        return (await response.ReadJsonAsync<IdBody>()).Id;
    }

    private async Task<Guid> RunNowAsync(int maxRetries = 1)
    {
        var sourceId = await FolderSourceAsync();
        var schedule = await ScheduleApi.CreateAsync(_admin, ScheduleApi.Body(sourceId, await ScheduleApi.DefaultPresetAsync(_admin),
            selection: new SelectionDto([], [new FolderSelectionDto("", true, false)]), maxRetries: maxRetries, isEnabled: false));
        var run = await (await _admin.PostAsync($"/api/schedules/{schedule.Id}/run", null)).ReadJsonAsync<RunCreated>();
        return run.RunId;
    }

    private Task<Details> DetailsAsync(Guid runId) => _admin.GetJsonAsync<Details>($"/api/runs/{runId}");

    private async Task<Details> UntilAsync(Guid runId, Func<Details, bool> done, int timeoutMs = 30_000)
    {
        Details details = null!;
        await EventuallyAsync(async () => done(details = await DetailsAsync(runId)), timeoutMs);
        return details;
    }

    [Test]
    public async Task EndToEnd_ScheduleRunNow_ModelExported()
    {
        _models.Model(@"ЖК\АР.rvt", 2022);
        _models.Model(@"ЖК\КР.rvt", 2022);
        _launcher.Then(FakeBehaviors.Ok(resultJson: """{"rvt":"Done","nwc":"NotRequested","rvtSizeBytes":432013312}"""));
        await AgentAsync();

        var runId = await RunNowAsync();

        var details = await UntilAsync(runId, d => d.Run.State == RunState.Completed);
        details.Jobs.Length.ShouldBe(2);
        details.Jobs.ShouldAllBe(j => j.Status == JobStatus.Succeeded && j.RevitYear == 2022);
        details.Jobs[0].Result.ShouldNotBeNull().GetProperty("rvtSizeBytes").GetInt64().ShouldBe(432013312);
        _launcher.Started.Count.ShouldBe(1); // один Revit выгрузил обе модели
        var log = await _admin.GetJsonAsync<LogLine[]>($"/api/jobs/{details.Jobs[0].Id}/log");
        log.ShouldContain(l => l.Text == "открытие модели");
    }

    [Test]
    public async Task EndToEnd_DashboardShowsBusyInstance()
    {
        _models.Model("АР.rvt", 2022);
        _launcher.Then(FakeBehaviors.Ok(delay: TimeSpan.FromSeconds(4)));
        await AgentAsync();

        await RunNowAsync();

        await EventuallyAsync(async () => (await _admin.GetJsonAsync<AgentRow[]>("/api/agents")).Single().Instances
            .Any(i => i.State == InstanceState.Busy && i.ModelName == "АР.rvt"));
    }

    [Test]
    public async Task EndToEnd_RevitInternalErrorRetriedInFreshRevit()
    {
        _models.Model("АР.rvt", 2022);
        _launcher.Then(FakeBehaviors.Ok(JobOutcome.InfrastructureFailure)).Then(FakeBehaviors.Ok());
        await AgentAsync();

        var runId = await RunNowAsync(maxRetries: 1);

        var job = (await UntilAsync(runId, d => d.Run.State == RunState.Completed, 60_000)).Jobs.Single();
        job.Status.ShouldBe(JobStatus.Succeeded);
        job.Attempt.ShouldBe(2);
        _launcher.Started.Count.ShouldBe(2); // повтор — в новом процессе Revit, не в том, где случилась ошибка
    }

    [Test]
    public async Task EndToEnd_HangRetriedThenSucceeds()
    {
        _models.Model("АР.rvt", 2022);
        _launcher.Then(FakeBehaviors.Hang()).Then(FakeBehaviors.Ok());
        await AgentAsync(silence: TimeSpan.FromSeconds(2));

        var runId = await RunNowAsync(maxRetries: 1);

        var job = (await UntilAsync(runId, d => d.Run.State == RunState.Completed, 60_000)).Jobs.Single();
        job.Status.ShouldBe(JobStatus.Succeeded);
        job.Attempt.ShouldBe(2);
        var log = await _admin.GetJsonAsync<LogLine[]>($"/api/jobs/{job.Id}/log");
        log.ShouldContain(l => l.Text.Contains("попытка 1 из 2"));
        await EventuallyAsync(async () => (await DbAsync(db => db.SystemEvents.ToListAsync())).Any(e => e.Text.Contains("убит: не отвечал")));
    }

    [Test]
    public async Task EndToEnd_KillFromUi()
    {
        _models.Model("АР.rvt", 2022);
        _launcher.Then(FakeBehaviors.UntilCancelled());
        var agent = await AgentAsync();
        var runId = await RunNowAsync(maxRetries: 0);
        await UntilAsync(runId, d => d.Jobs.Any(j => j.Status == JobStatus.Running));

        (await _admin.PostAsync($"/api/agents/{agent.Config.AgentId}/instances/1/kill", null)).EnsureSuccessStatusCode();

        var job = (await UntilAsync(runId, d => d.Run.State == RunState.Completed)).Jobs.Single();
        job.Status.ShouldBe(JobStatus.Failed);
        job.Note.ShouldNotBeNull().ShouldContain("убит вручную");
    }

    [Test]
    public async Task EndToEnd_CancelRun()
    {
        _models.Model("АР.rvt", 2022);
        _launcher.Then(FakeBehaviors.UntilCancelled());
        await AgentAsync();
        var runId = await RunNowAsync();
        await UntilAsync(runId, d => d.Jobs.Any(j => j.Status == JobStatus.Running));

        (await _admin.PostAsync($"/api/runs/{runId}/cancel", null)).EnsureSuccessStatusCode();

        var details = await UntilAsync(runId, d => d.Run.State == RunState.Cancelled);
        details.Jobs.Single().Status.ShouldBe(JobStatus.Cancelled);
    }

    [Test]
    public async Task EndToEnd_Draining_TakesNothing()
    {
        _models.Model("АР.rvt", 2022);
        var agent = await AgentAsync();
        (await _admin.PostJsonAsync($"/api/agents/{agent.Config.AgentId}/drain", new { draining = true })).EnsureSuccessStatusCode();

        var runId = await RunNowAsync();

        await UntilAsync(runId, d => d.Jobs.Length == 1);
        await Task.Delay(3000);
        (await DetailsAsync(runId)).Jobs.Single().Status.ShouldBe(JobStatus.Queued);
        _launcher.Started.ShouldBeEmpty();
    }

    [Test]
    public async Task ServerReconnect_CompletesAfterReconnect()
    {
        _models.Model("АР.rvt", 2022);
        _launcher.Then(FakeBehaviors.Ok(delay: TimeSpan.FromSeconds(4)));
        var agent = await AgentAsync();
        var runId = await RunNowAsync();
        await UntilAsync(runId, d => d.Jobs.Any(j => j.Status == JobStatus.Running));

        Server.Services.GetRequiredService<AgentRegistry>().Abort(agent.Config.AgentId); // обрыв связи посреди выгрузки

        var job = (await UntilAsync(runId, d => d.Run.State == RunState.Completed, 60_000)).Jobs.Single();
        job.Status.ShouldBe(JobStatus.Succeeded);
    }

    [Test]
    public async Task SourceTree_FromUi_ProxiedToAgent()
    {
        _models.Model(@"ЖК\АР.rvt", 2022);
        await AgentAsync();
        var sourceId = await FolderSourceAsync();

        var entries = await _admin.GetJsonAsync<SourceEntryDto[]>($"/api/sources/{sourceId}/tree?path=");

        entries.ShouldHaveSingleItem().Name.ShouldBe("ЖК");
    }
}
