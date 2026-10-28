using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Shouldly;
using ToadFlow.Agent.Instances;
using ToadFlow.Agent.Revit;
using ToadFlow.Agent.Tests.Infrastructure;
using ToadFlow.Contracts;
using ToadFlow.Contracts.Pipe;

namespace ToadFlow.Agent.Tests.Instances;

public class InstanceManagerTests : IAsyncDisposable
{
    internal static readonly RevitInstallation[] Revits =
    [
        new(2022, @"C:\Program Files\Autodesk\Revit 2022\Revit.exe", false),
        new(2024, @"C:\Program Files\Autodesk\Revit 2024\Revit.exe", true)
    ];

    internal static AgentSettingsDto Settings(int recycleAfter = 20, int memoryMb = 12000, int silence = 10) =>
        new([new VersionLimitDto(2022, 2), new VersionLimitDto(2024, 1)], silence, recycleAfter, memoryMb, false);

    private readonly FakeTimeProvider _time = new(DateTimeOffset.UtcNow);
    private readonly FakeLauncher _launcher = new();
    private readonly InstanceManager _manager;

    public InstanceManagerTests() => _manager = new InstanceManager(_launcher, Revits, _time, NullLogger<InstanceManager>.Instance);

    public async ValueTask DisposeAsync()
    {
        await _manager.DisposeAsync();
        await _launcher.DisposeAddinsAsync();
    }

    private Task<RevitInstance> AcquireAsync(int year) => _time.DriveAsync(_manager.AcquireAsync(year, CancellationToken.None), TimeSpan.FromSeconds(1), TimeSpan.FromMinutes(10));

    [Test]
    public async Task Start_WaitsForHello_ThenReserved()
    {
        var instance = await AcquireAsync(2022);

        instance.State.ShouldBe(InstanceState.Busy);
        instance.Year.ShouldBe(2022);
        instance.HasNwcExporter.ShouldBeTrue(); // из hello аддина, а не из догадки по манифестам
        _launcher.Started.ShouldHaveSingleItem().Exe.ShouldBe(Revits[0].RevitExe);
    }

    [Test]
    public async Task Release_ThenAcquire_ReusesInstance()
    {
        var first = await AcquireAsync(2022);
        _manager.Release(first, Settings());
        first.State.ShouldBe(InstanceState.Idle);

        var second = await AcquireAsync(2022);

        second.ShouldBeSameAs(first);
        _launcher.Started.Count.ShouldBe(1);
    }

    [Test]
    public async Task Starting_ReportsReservedJob_SoServerKeepsLease()
    {
        _launcher.Then(FakeBehaviors.Ok(), hello: false);
        var job = new JobAssignmentDto(Guid.NewGuid(), ModuleKinds.Export, "АР.rvt", "{}", 30, 1);
        using var stop = new CancellationTokenSource();

        var acquire = _manager.AcquireAsync(2022, stop.Token, job);

        // Revit ещё стартует, а heartbeat уже несёт задание — сервер продлевает его аренду и не вернёт его в очередь.
        var status = _manager.Statuses().ShouldHaveSingleItem();
        status.State.ShouldBe(InstanceState.Starting);
        status.JobId.ShouldBe(job.JobId);
        status.Stage.ShouldBe("Запуск Revit");

        await stop.CancelAsync();
        await Should.ThrowAsync<OperationCanceledException>(() => acquire);
    }

    [Test]
    public async Task StartTimeout_KillsAndThrows()
    {
        _launcher.Then(FakeBehaviors.Ok(), hello: false);

        var error = await Should.ThrowAsync<RevitStartException>(() => AcquireAsync(2022));

        error.Message.ShouldContain("5 мин");
        _launcher.Started.Single().Process.Killed.ShouldBeTrue();
        _manager.Instances.ShouldBeEmpty();
    }

    [Test]
    public async Task StartTimeout_BackoffBeforeNextStart()
    {
        _launcher.Then(FakeBehaviors.Ok(), hello: false).Then(FakeBehaviors.Ok());
        await Should.ThrowAsync<RevitStartException>(() => AcquireAsync(2022));

        await Should.ThrowAsync<RevitStartException>(() => _manager.AcquireAsync(2022, CancellationToken.None));
        _launcher.Started.Count.ShouldBe(1);

        _time.Advance(TimeSpan.FromSeconds(61));
        (await AcquireAsync(2022)).State.ShouldBe(InstanceState.Busy);
        _launcher.Started.Count.ShouldBe(2);
    }

    /// <summary>«Revit» до загрузки аддина показывает окно и ждёт, пока его закроют.</summary>
    private static FakeStartup BlockedBy(string title, string[] texts, string[] buttons, bool closes = true, bool? ready = true) =>
        new(Ready: ready, BeforeHello: async process => await process.Dialogs.Show(title, texts, buttons, closes).Closed.Task);

    [Test]
    public async Task Start_UnsignedAddinDialog_AnsweredAlwaysLoad_ThenReady()
    {
        _launcher.Then(FakeBehaviors.Ok(), BlockedBy("Security - Unsigned Add-In",
            [@"Location: C:\ProgramData\Autodesk\Revit\Addins\2022\ToadFlow.Revit.Addin\ToadFlow.Revit.Addin.dll"], ["Always Load", "Load Once", "Do Not Load"]));

        var instance = await AcquireAsync(2022);

        instance.State.ShouldBe(InstanceState.Busy);
        ((FakeRevitProcess)instance.Process).Dialogs.Answers.ShouldHaveSingleItem().Action.ShouldBe(DialogAction.AlwaysLoad);
    }

    [Test]
    [Arguments("Аппаратное ускорение недоступно.")]
    [Arguments("Неверный путь к шаблонам семейств.")]
    public async Task Start_KnownWarning_Dismissed_ThenReady(string text)
    {
        _launcher.Then(FakeBehaviors.Ok(), BlockedBy("Autodesk Revit 2022", [text], ["ОК", "Подробнее"]));

        var instance = await AcquireAsync(2022);

        ((FakeRevitProcess)instance.Process).Dialogs.Answers.ShouldHaveSingleItem().Action.ShouldBe(DialogAction.Dismiss);
    }

    [Test]
    public async Task Start_UnknownQuestion_NotAnswered_FailsAfterGraceWithWindowText()
    {
        _launcher.Then(FakeBehaviors.Ok(), BlockedBy("Autodesk Licensing", ["Выберите тип лицензии"], ["Войти", "Отмена"]));
        var started = _time.GetUtcNow();

        var error = await Should.ThrowAsync<RevitStartException>(() => AcquireAsync(2022));

        error.Message.ShouldContain("ждёт ответа в окне «Autodesk Licensing»: Выберите тип лицензии");
        (_time.GetUtcNow() - started).ShouldBeLessThan(InstanceManager.StartTimeout);
        var process = _launcher.Started.Single().Process;
        process.Killed.ShouldBeTrue();
        process.Dialogs.Answers.ShouldBeEmpty();
        _manager.StartDeferred(2022).ShouldBeTrue();
    }

    [Test]
    public async Task Start_ThirdPartyUnsignedAddin_NotAnswered()
    {
        _launcher.Then(FakeBehaviors.Ok(), BlockedBy("Безопасность — Надстройка без подписи", [@"Расположение: C:\Addins\Vendor.dll"],
            ["Всегда загружать", "Загрузить один раз", "Не загружать"]));

        var error = await Should.ThrowAsync<RevitStartException>(() => AcquireAsync(2022));

        error.Message.ShouldContain("неподписанная сторонняя надстройка");
        _launcher.Started.Single().Process.Dialogs.Answers.ShouldBeEmpty();
    }

    [Test]
    public async Task Start_FatalError_FailsAtOnce()
    {
        _launcher.Then(FakeBehaviors.Ok(), BlockedBy("Autodesk Revit 2022", ["В Revit произошла серьёзная ошибка."], ["Сохранить", "Закрыть"]));
        var started = _time.GetUtcNow();

        var error = await Should.ThrowAsync<RevitStartException>(() => AcquireAsync(2022));

        error.Message.ShouldContain("серьёзная ошибка Revit");
        (_time.GetUtcNow() - started).ShouldBeLessThan(DialogWatcher.AnswerGrace);
    }

    [Test]
    public async Task Start_HelloWithoutReady_TimesOutAsNotFinishedStarting()
    {
        _launcher.Then(FakeBehaviors.Ok(), new FakeStartup(Ready: false));

        var error = await Should.ThrowAsync<RevitStartException>(() => AcquireAsync(2022));

        error.Message.ShouldContain("5 мин");
        error.Message.ShouldContain("аддин ToadFlow загружен, но Revit не закончил запуск");
    }

    [Test]
    public async Task Start_NoHello_TimeoutNamesOpenWindow()
    {
        _launcher.Then(FakeBehaviors.Ok(), BlockedBy("Revit", ["Идёт загрузка"], []));

        var error = await Should.ThrowAsync<RevitStartException>(() => AcquireAsync(2022));

        error.Message.ShouldContain("аддин ToadFlow не загрузился; открыто окно «Revit»: Идёт загрузка");
    }

    [Test]
    public async Task Start_AddinWithReady_NotGivenJobsBeforeReady()
    {
        _launcher.Then(FakeBehaviors.Ok(), new FakeStartup(Ready: false));
        var acquire = _manager.AcquireAsync(2022, CancellationToken.None);

        for (var second = 0; second < 30; second++)
        {
            await Task.Delay(25);
            _time.Advance(TimeSpan.FromSeconds(1));
        }

        acquire.IsCompleted.ShouldBeFalse();
        _manager.Statuses().Single().Stage.ShouldBe("Запуск Revit: аддин загружен");

        await _launcher.Addins.Single().SendAsync(new ReadyMessage());
        (await _time.DriveAsync(acquire, TimeSpan.FromSeconds(1), TimeSpan.FromMinutes(1))).State.ShouldBe(InstanceState.Busy);
    }

    [Test]
    public async Task Start_JobCancelled_StopsStartingWithoutBackoff()
    {
        _launcher.Then(FakeBehaviors.Ok(), hello: false);
        var cancelled = false;
        var acquire = _manager.AcquireAsync(2022, CancellationToken.None, abandoned: () => cancelled);
        await Task.Delay(100);
        cancelled = true;

        var error = await Should.ThrowAsync<RevitStartException>(() => _time.DriveAsync(acquire, TimeSpan.FromSeconds(1), TimeSpan.FromMinutes(1)));

        error.Message.ShouldContain("отменено");
        _launcher.Started.Single().Process.Killed.ShouldBeTrue();
        _manager.StartDeferred(2022).ShouldBeFalse();
    }

    [Test]
    public async Task StartDeferred_OnlyWithinBackoff()
    {
        _launcher.Then(FakeBehaviors.Ok(), hello: false);
        await Should.ThrowAsync<RevitStartException>(() => AcquireAsync(2022));

        _manager.StartDeferred(2022).ShouldBeTrue();
        _manager.StartDeferred(2024).ShouldBeFalse();
        _time.Advance(InstanceManager.StartBackoff);
        _manager.StartDeferred(2022).ShouldBeFalse();
    }

    [Test]
    public async Task UnknownYear_Throws() =>
        await Should.ThrowAsync<RevitStartException>(() => _manager.AcquireAsync(2026, CancellationToken.None));

    [Test]
    public async Task Slots_SharedAcrossYears_MaxThree()
    {
        var a = await AcquireAsync(2022);
        var b = await AcquireAsync(2022);
        var c = await AcquireAsync(2024);

        new[] { a.Slot, b.Slot, c.Slot }.Order().ShouldBe([1, 2, 3]);
        await Should.ThrowAsync<RevitStartException>(() => _manager.AcquireAsync(2024, CancellationToken.None));
    }

    [Test]
    public async Task Slots_FollowMaxSlots()
    {
        _manager.MaxSlots = 5;
        var slots = new List<int>();
        for (var i = 0; i < 5; i++) slots.Add((await AcquireAsync(i % 2 == 0 ? 2022 : 2024)).Slot);

        slots.Order().ShouldBe([1, 2, 3, 4, 5]);
        await Should.ThrowAsync<RevitStartException>(() => _manager.AcquireAsync(2022, CancellationToken.None));
    }

    [Test]
    public async Task Recycle_AfterNModels_Kills()
    {
        var instance = await AcquireAsync(2022);

        _manager.Release(instance, Settings(recycleAfter: 1));

        ((FakeRevitProcess)instance.Process).Killed.ShouldBeTrue();
        _manager.Instances.ShouldBeEmpty();
    }

    [Test]
    public async Task Recycle_MemoryAbove_Kills()
    {
        var instance = await AcquireAsync(2022);
        ((FakeRevitProcess)instance.Process).WorkingSetMb = 15000;

        _manager.Release(instance, Settings(memoryMb: 12000));

        ((FakeRevitProcess)instance.Process).Killed.ShouldBeTrue();
    }

    [Test]
    public async Task CloseIdle_After10Minutes()
    {
        var instance = await AcquireAsync(2022);
        _manager.Release(instance, Settings());

        _time.Advance(TimeSpan.FromMinutes(9));
        _manager.CloseIdle();
        _manager.Instances.Count.ShouldBe(1);

        _time.Advance(TimeSpan.FromMinutes(2));
        _manager.CloseIdle();
        _manager.Instances.ShouldBeEmpty();
        ((FakeRevitProcess)instance.Process).Killed.ShouldBeTrue();
    }

    [Test]
    public async Task ProcessExitedWhileIdle_Forgotten()
    {
        var instance = await AcquireAsync(2022);
        _manager.Release(instance, Settings());

        ((FakeRevitProcess)instance.Process).Exit();
        await Task.Delay(100);

        _manager.Instances.ShouldBeEmpty();
    }

    [Test]
    public async Task Statuses_ForHeartbeat()
    {
        var instance = await AcquireAsync(2024);

        var status = _manager.Statuses().ShouldHaveSingleItem();

        status.Slot.ShouldBe(instance.Slot);
        status.Year.ShouldBe(2024);
        status.Pid.ShouldBe(instance.Process.Pid);
    }

    [Test]
    public async Task RecycleReason_ReleaseKillsInsteadOfIdle_NextAcquireStartsNewRevit()
    {
        var first = await AcquireAsync(2022);
        first.RecycleReason = "модель не закрылась после задания";

        _manager.Release(first, Settings());
        var second = await AcquireAsync(2022);

        ((FakeRevitProcess)first.Process).Killed.ShouldBeTrue();
        first.KillReason.ShouldBe("модель не закрылась после задания");
        second.ShouldNotBeSameAs(first);
        _launcher.Started.Count.ShouldBe(2);
    }

    [Test]
    public async Task CloseIdleForMemory_ClosesOnlyIdle()
    {
        var idle = await AcquireAsync(2022);
        var busy = await AcquireAsync(2022);
        _manager.Release(idle, Settings());

        _manager.CloseIdleForMemory().ShouldBe(1);

        ((FakeRevitProcess)idle.Process).Killed.ShouldBeTrue();
        ((FakeRevitProcess)busy.Process).Killed.ShouldBeFalse();
        _manager.Instances.ShouldBe([busy]);
    }

    [Test]
    public async Task ExpectedJobMb_LearnsFromRecentPeaks()
    {
        _manager.ExpectedJobMb(4096).ShouldBe(4096);
        var instance = await AcquireAsync(2022);
        instance.PeakWorkingSetMb = 14000;

        _manager.Release(instance, Settings());

        _manager.ExpectedJobMb(4096).ShouldBe(14000);
        for (var i = 0; i < InstanceManager.RecentPeaks; i++) _manager.RecordPeak(3000);
        _manager.ExpectedJobMb(4096).ShouldBe(4096);
    }
}
