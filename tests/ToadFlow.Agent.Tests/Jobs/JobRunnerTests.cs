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
    public async Task Run_PassesModuleKindToAddin()
    {
        string? kind = null;
        _launcher.Then(async (addin, job, _) =>
        {
            kind = job.ModuleKind;
            await addin.ResultAsync(job, JobOutcome.Succeeded);
        });

        var (completion, _) = await RunAsync(Assignment() with { ModuleKind = ModuleKinds.Loi }, InstanceManagerTests.Settings());

        completion.Outcome.ShouldBe(JobOutcome.Succeeded);
        kind.ShouldBe(ModuleKinds.Loi);
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

    /// <summary>Аддин сообщил «Экспорт NWC» и молчит, пока поток Revit API в Document.Export; процесс тратит полъядра.</summary>
    private FakeJobBehavior LongNwcExport(TimeSpan duration) => async (addin, job, _) =>
    {
        var start = _time.GetUtcNow();
        addin.Process.Sampler = () => new ProcessSample(TimeSpan.FromTicks((_time.GetUtcNow() - start).Ticks / 2), 0, 9000, 9500);
        await addin.ProgressAsync(job, "Экспорт NWC", "экспорт NWC");
        while (_time.GetUtcNow() - start < duration) await Task.Delay(10);
        await addin.ResultAsync(job, JobOutcome.Succeeded, null, """{"nwc":"Done"}""");
    };

    [Test]
    public async Task LongNwcExport_WithoutMessages_IsNotTakenForHang()
    {
        _launcher.Then(LongNwcExport(TimeSpan.FromMinutes(25)));

        var (completion, instance) = await RunAsync(Assignment(timeoutMinutes: 120), InstanceManagerTests.Settings(silence: 10));

        completion.Outcome.ShouldBe(JobOutcome.Succeeded);
        _reporter.Killed.ShouldBeEmpty();
        ((FakeRevitProcess)instance.Process).Killed.ShouldBeFalse();
        _reporter.Lines.ShouldContain(l => l.Stage == JobRunner.DiagnosticsStage && l.Text.Contains("процесс работает") && l.Text.Contains("Экспорт NWC"));
        _reporter.Lines.ShouldContain(l => l.Stage == JobRunner.DiagnosticsStage && l.Text.Contains("итог Succeeded") && l.Text.Contains("Экспорт NWC 2"));
        instance.PeakWorkingSetMb.ShouldBe(9000);
    }

    [Test]
    public async Task Hang_WithIdleProcess_IsStillKilledAfterSilenceLimit()
    {
        _launcher.Then(async (addin, job, _) =>
        {
            addin.Process.Sampler = () => new ProcessSample(TimeSpan.FromSeconds(42), 1_000_000, 5000, 5200); // счётчики не растут
            await addin.ProgressAsync(job, "Экспорт NWC", "экспорт NWC");
        });

        var (completion, _) = await RunAsync(Assignment(timeoutMinutes: 120), InstanceManagerTests.Settings(silence: 10));

        completion.Outcome.ShouldBe(JobOutcome.InfrastructureFailure);
        _reporter.Killed.ShouldHaveSingleItem().Reason.ShouldContain("не отвечал 10 мин на этапе «Экспорт NWC»");
        _reporter.Lines.ShouldContain(l => l.Text.Contains("не работает"));
    }

    [Test]
    public async Task MessageDialog_DuringNwcExport_IsClosed_JobEndsWithoutKill()
    {
        const string message = "«Navisworks»: Не найдена подходящая геометрия.";
        _launcher.Then(async (addin, job, _) =>
        {
            var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            addin.Process.Sampler = () => new ProcessSample(TimeSpan.FromSeconds(42), 1_000_000, 5000, 5200); // ждёт окна — простаивает
            addin.Process.Dialogs = () =>
            {
                if (closed.Task.IsCompleted) return [];
                closed.SetResult();
                return [message];
            };
            await addin.ProgressAsync(job, "Экспорт NWC", "экспорт NWC");
            await closed.Task;
            await addin.ResultAsync(job, JobOutcome.Failed, "NWC: Navisworks Exporter не создал файл NWC", """{"nwc":"Failed"}""");
        });

        var (completion, instance) = await RunAsync(Assignment(timeoutMinutes: 120), InstanceManagerTests.Settings(silence: 10));

        completion.Outcome.ShouldBe(JobOutcome.Failed);
        completion.Note.ShouldBe($"NWC: Navisworks Exporter не создал файл NWC; агент закрыл окно Revit: {message}");
        _reporter.Killed.ShouldBeEmpty();
        ((FakeRevitProcess)instance.Process).Killed.ShouldBeFalse();
        _reporter.Lines.ShouldContain(l => l.Text.Contains("агент закрыл окно") && l.Text.Contains("Экспорт NWC"));
    }

    [Test]
    public async Task BusyForever_IsKilledByModelTimeout()
    {
        // Процесс в бесконечном цикле: работа не может продлевать задание бесконечно.
        _launcher.Then(LongNwcExport(TimeSpan.FromDays(1)));

        var (completion, _) = await RunAsync(Assignment(timeoutMinutes: 45), InstanceManagerTests.Settings(silence: 10));

        completion.Outcome.ShouldBe(JobOutcome.InfrastructureFailure);
        _reporter.Killed.ShouldHaveSingleItem().Reason.ShouldContain("таймаут 45 мин");
    }

    [Test]
    public async Task Diagnostics_StartAndCrashStageReachJobLog()
    {
        _launcher.Then(FakeBehaviors.Crash());

        await RunAsync(Assignment(), InstanceManagerTests.Settings());

        _reporter.Lines.ShouldContain(l => l.Stage == JobRunner.DiagnosticsStage && l.Text.Contains("PID") && l.Text.Contains("Revit 2022") &&
                                           l.Text.Contains("попытка 1"));
        _reporter.Lines.ShouldContain(l => l.Text.Contains("итог InfrastructureFailure") &&
                                           l.Text.Contains("Revit завершился во время задания на этапе «Открытие»"));
    }

    [Test]
    public async Task Diagnostics_StartLineIsSentBeforeAddinGetsTheJob()
    {
        var linesWhenJobArrived = -1;
        _launcher.Then(async (addin, job, _) =>
        {
            linesWhenJobArrived = _reporter.Lines.Count(l => l.Stage == JobRunner.DiagnosticsStage);
            await addin.ResultAsync(job, JobOutcome.Succeeded);
        });

        await RunAsync(Assignment(), InstanceManagerTests.Settings());

        linesWhenJobArrived.ShouldBe(1);
    }

    [Test]
    public async Task RecycleReason_InstanceGetsNoMoreJobs()
    {
        _launcher.Then(async (addin, job, _) =>
            await addin.SendAsync(new ResultMessage { JobId = job.JobId, Outcome = JobOutcome.Failed, Note = "ошибка", RecycleReason = "модель не закрылась после задания" }));

        var (completion, instance) = await RunAsync(Assignment(), InstanceManagerTests.Settings());
        _manager.Release(instance, InstanceManagerTests.Settings());

        completion.Outcome.ShouldBe(JobOutcome.Failed);
        instance.RecycleReason.ShouldBe("модель не закрылась после задания");
        ((FakeRevitProcess)instance.Process).Killed.ShouldBeTrue();
        _manager.Instances.ShouldBeEmpty();
        _manager.HasFreeSlot(2022).ShouldBeTrue(); // следующее задание получит новый Revit
    }
}
