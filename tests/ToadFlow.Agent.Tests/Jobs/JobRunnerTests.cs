using System.Collections.Concurrent;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Shouldly;
using ToadFlow.Agent.Instances;
using ToadFlow.Agent.Jobs;
using ToadFlow.Agent.Tests.Infrastructure;
using ToadFlow.Agent.Tests.Instances;
using ToadFlow.Contracts;
using ToadFlow.Contracts.Pipe;

namespace ToadFlow.Agent.Tests.Jobs;

public class JobRunnerTests : IAsyncDisposable
{
    private sealed class FakeReporter : IJobReporter
    {
        public ConcurrentBag<JobLogDto> Lines { get; } = [];
        public ConcurrentBag<(int Slot, string Reason)> Killed { get; } = [];

        public Task ProgressAsync(JobProgressDto progress)
        {
            foreach (var line in progress.Lines) Lines.Add(line);
            return Task.CompletedTask;
        }

        public Task InstanceKilledAsync(int slot, string reason)
        {
            Killed.Add((slot, reason));
            return Task.CompletedTask;
        }
    }

    private readonly FakeTimeProvider _time = new(DateTimeOffset.UtcNow);
    private readonly FakeLauncher _launcher = new();
    private readonly FakeReporter _reporter = new();
    private readonly InstanceManager _manager;
    private readonly JobRunner _runner;

    public JobRunnerTests()
    {
        _manager = new InstanceManager(_launcher, InstanceManagerTests.Revits, _time, NullLogger<InstanceManager>.Instance);
        _runner = new JobRunner(_manager, _time, NullLogger<JobRunner>.Instance);
    }

    public async ValueTask DisposeAsync()
    {
        await _manager.DisposeAsync();
        await _launcher.DisposeAddinsAsync();
    }

    private static JobAssignmentDto Assignment(int timeoutMinutes = 30) =>
        new(Guid.NewGuid(), ModuleKinds.Export, "ЖК-Север_АР.rvt", """{"modelName":"ЖК-Север_АР.rvt"}""", timeoutMinutes, 1);

    private async Task<(JobCompletionDto Completion, RevitInstance Instance)> RunAsync(JobAssignmentDto assignment, AgentSettingsDto settings,
        Action<RevitInstance>? during = null)
    {
        var instance = await _time.DriveAsync(_manager.AcquireAsync(2022, CancellationToken.None), TimeSpan.FromSeconds(1), TimeSpan.FromMinutes(6));
        var run = _runner.RunAsync(instance, assignment, settings, _reporter, CancellationToken.None);
        if (during is not null)
        {
            await Task.Delay(300);
            during(instance);
        }

        return (await _time.DriveAsync(run, TimeSpan.FromSeconds(20), TimeSpan.FromHours(2)), instance);
    }

    [Test]
    public async Task Run_Ok_ReturnsAddinResultAndForwardsProgress()
    {
        _launcher.Then(FakeBehaviors.Ok(resultJson: """{"rvt":"Done"}"""));

        var (completion, instance) = await RunAsync(Assignment(), InstanceManagerTests.Settings());

        completion.Outcome.ShouldBe(JobOutcome.Succeeded);
        completion.ResultJson.ShouldBe("""{"rvt":"Done"}""");
        _reporter.Lines.ShouldContain(l => l.Text == "открытие модели" && l.Stage == "Открытие");
        instance.State.ShouldBe(InstanceState.Busy); // освобождает экземпляр вызывающий
    }

    [Test]
    public async Task Run_SetsBusyStatusWithModelAndStage()
    {
        _launcher.Then(FakeBehaviors.Ok(delay: TimeSpan.FromSeconds(1)));
        InstanceStatusDto? seen = null;

        await RunAsync(Assignment(), InstanceManagerTests.Settings(), during: _ => seen = _manager.Statuses().Single());

        seen.ShouldNotBeNull();
        seen.JobId.ShouldNotBeNull();
        seen.Stage.ShouldBe("Открытие");
        _manager.Instances.Single().ModelName.ShouldBe("ЖК-Север_АР.rvt");
    }

    [Test]
    public async Task Silence_KillsAndReportsInfrastructureFailure()
    {
        _launcher.Then(FakeBehaviors.Hang());

        var (completion, instance) = await RunAsync(Assignment(timeoutMinutes: 120), InstanceManagerTests.Settings(silence: 10));

        completion.Outcome.ShouldBe(JobOutcome.InfrastructureFailure);
        completion.Note.ShouldNotBeNull().ShouldContain("не отвечал");
        ((FakeRevitProcess)instance.Process).Killed.ShouldBeTrue();
        _reporter.Killed.ShouldHaveSingleItem().Reason.ShouldContain("не отвечал 10 мин");
        _manager.Instances.ShouldBeEmpty();
    }

    [Test]
    public async Task Silence_MarksInstanceUnresponsiveBeforeKill()
    {
        _launcher.Then(FakeBehaviors.Hang());
        var instance = await _time.DriveAsync(_manager.AcquireAsync(2022, CancellationToken.None), TimeSpan.FromSeconds(1), TimeSpan.FromMinutes(6));
        var run = _runner.RunAsync(instance, Assignment(120), InstanceManagerTests.Settings(silence: 10), _reporter, CancellationToken.None);
        await Task.Delay(300);

        for (var i = 0; i < 12 && instance.State != InstanceState.Unresponsive; i++)
        {
            _time.Advance(TimeSpan.FromSeconds(20));
            await Task.Delay(50);
        }

        instance.State.ShouldBe(InstanceState.Unresponsive);
        await _time.DriveAsync(run, TimeSpan.FromSeconds(20), TimeSpan.FromHours(1));
    }

    [Test]
    public async Task AgentStopping_StopsWaitingAtOnce()
    {
        _launcher.Then(FakeBehaviors.Hang());
        var instance = await _time.DriveAsync(_manager.AcquireAsync(2022, CancellationToken.None), TimeSpan.FromSeconds(1), TimeSpan.FromMinutes(6));
        using var stop = new CancellationTokenSource();
        var run = _runner.RunAsync(instance, Assignment(120), InstanceManagerTests.Settings(silence: 10), _reporter, stop.Token);
        await Task.Delay(300);

        await stop.CancelAsync();

        // без реального времени: цикл не должен крутиться, дожидаясь, пока Revit убьют при остановке
        await Should.ThrowAsync<OperationCanceledException>(run.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Test]
    public async Task ModelTimeout_Kills()
    {
        _launcher.Then(FakeBehaviors.Hang());

        var (completion, _) = await RunAsync(Assignment(timeoutMinutes: 30), InstanceManagerTests.Settings(silence: 600));

        completion.Outcome.ShouldBe(JobOutcome.InfrastructureFailure);
        completion.Note.ShouldNotBeNull().ShouldContain("таймаут 30 мин");
        _reporter.Killed.ShouldHaveSingleItem();
    }

    [Test]
    public async Task ProcessExit_ReportsInfrastructureFailure()
    {
        _launcher.Then(FakeBehaviors.Crash());

        var (completion, _) = await RunAsync(Assignment(), InstanceManagerTests.Settings());

        completion.Outcome.ShouldBe(JobOutcome.InfrastructureFailure);
        completion.Note.ShouldNotBeNull().ShouldContain("Revit завершился");
        _reporter.Killed.ShouldBeEmpty();
        _manager.Instances.ShouldBeEmpty();
    }

    [Test]
    public async Task Cancel_SendsCancel_AddinReturnsCancelled()
    {
        _launcher.Then(FakeBehaviors.UntilCancelled());
        var assignment = Assignment();

        var (completion, _) = await RunAsync(assignment, InstanceManagerTests.Settings(), during: _ => _runner.Cancel(assignment.JobId));

        completion.Outcome.ShouldBe(JobOutcome.Cancelled);
        _launcher.Addins.Single().Received.ShouldContain(m => m is CancelMessage);
    }

    [Test]
    public async Task Kill_Manual_ReportsInfrastructureFailure()
    {
        _launcher.Then(FakeBehaviors.UntilCancelled());

        var (completion, _) = await RunAsync(Assignment(), InstanceManagerTests.Settings(), during: instance => _manager.Kill(instance.Slot, "убит вручную"));

        completion.Outcome.ShouldBe(JobOutcome.InfrastructureFailure);
        completion.Note.ShouldNotBeNull().ShouldContain("убит вручную");
        _reporter.Killed.ShouldBeEmpty(); // ручное убийство сервер записал сам
    }
}
