using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Shouldly;
using ToadFlow.Agent.Instances;
using ToadFlow.Agent.Revit;
using ToadFlow.Agent.Tests.Infrastructure;
using ToadFlow.Contracts;

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
}
