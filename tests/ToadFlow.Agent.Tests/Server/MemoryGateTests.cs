using Shouldly;
using ToadFlow.Agent.Server;

namespace ToadFlow.Agent.Tests.Server;

public class MemoryGateTests
{
    private static readonly (long CurrentMb, long PeakMb)[] None = [];

    [Test]
    public void EnoughMemory_Allowed() =>
        MemoryGate.Check(new RamSnapshot(65536, 40000), [(8000, 8000)], null, 6000, 4096).Allowed.ShouldBeTrue();

    [Test]
    public void BusyRevitsWillGrow_NewJobRefused()
    {
        // Свободно 14 ГБ, но два Revit только открывают модели: каждый дорастёт до 6 ГБ.
        var decision = MemoryGate.Check(new RamSnapshot(32768, 14000), [(1000, 1000), (1500, 1500)], null, 6000, 4096);

        decision.Allowed.ShouldBeFalse();
        decision.Reason.ShouldContain("не хватает памяти");
        decision.Reason.ShouldContain("вырастут занятые Revit на 9500 МБ");
    }

    [Test]
    public void PeakOfCurrentJob_CountsWhenAboveEstimate()
    {
        // Revit уже был на 12 ГБ и сейчас временно отдал память — закладываем его пик.
        MemoryGate.Check(new RamSnapshot(65536, 15000), [(5000, 12000)], null, 6000, 4096).Allowed.ShouldBeFalse();
        MemoryGate.Check(new RamSnapshot(65536, 15000), [(5000, 5000)], null, 6000, 4096).Allowed.ShouldBeTrue();
    }

    [Test]
    public void ReserveIsKept()
    {
        MemoryGate.Check(new RamSnapshot(32768, 10000), [(8000, 8000)], null, 6000, 4096).Allowed.ShouldBeFalse();
        MemoryGate.Check(new RamSnapshot(32768, 10097), [(8000, 8000)], null, 6000, 4096).Allowed.ShouldBeTrue();
    }

    [Test]
    public void IdleRevitReuse_NeedsOnlyItsGrowth()
    {
        MemoryGate.Check(new RamSnapshot(32768, 7000), [(8000, 8000)], idleReuseMb: 5000, 6000, 4096).Allowed.ShouldBeTrue();
        MemoryGate.Check(new RamSnapshot(32768, 7000), [(8000, 8000)], idleReuseMb: null, 6000, 4096).Allowed.ShouldBeFalse();
    }

    [Test]
    public void NothingRunning_OneJobAlwaysAllowed_QueueNeverStarves()
    {
        var decision = MemoryGate.Check(new RamSnapshot(16384, 3000), None, null, 6000, 4096);

        decision.Allowed.ShouldBeTrue();
        decision.Reason.ShouldContain("агент свободен");
    }
}
