using Shouldly;
using ToadFlow.Agent.Instances;
using ToadFlow.Agent.Jobs;

namespace ToadFlow.Agent.Tests.Jobs;

public class HangWatchTests
{
    private static readonly DateTimeOffset T0 = DateTimeOffset.Parse("2026-10-27T10:00:00Z");
    private static readonly TimeSpan Limit = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan QuietAfter = TimeSpan.FromMinutes(1);

    /// <summary>Сообщение было в T0, дальше <paramref name="minutes" /> минут только счётчики процесса.</summary>
    private static (HangWatch Watch, DateTimeOffset Now) Run(double minutes, double cpuShare, double ioBytesPerSecond = 0)
    {
        var watch = new HangWatch(Limit);
        var now = T0;
        for (var t = TimeSpan.Zero; t <= TimeSpan.FromMinutes(minutes); t += HangWatch.SampleEvery)
        {
            now = T0 + t;
            watch.Observe(now, new ProcessSample(t * cpuShare, (ulong)(t.TotalSeconds * ioBytesPerSecond), 8000, 8500));
        }

        return (watch, now);
    }

    [Test]
    public void RecentMessage_Active()
    {
        var (watch, now) = Run(0.5, 0);
        watch.Judge(now, T0, QuietAfter).ShouldBe(HangVerdict.Active);
    }

    [Test]
    public void SilentButBusyCpu_LongPastLimit_IsWorking()
    {
        var (watch, now) = Run(40, cpuShare: 1.0);
        watch.Judge(now, T0, QuietAfter).ShouldBe(HangVerdict.WorkingSilently);
    }

    [Test]
    public void SilentButWritingFile_IsWorking()
    {
        var (watch, now) = Run(15, cpuShare: 0.01, ioBytesPerSecond: 5 * 1024 * 1024);
        watch.Judge(now, T0, QuietAfter).ShouldBe(HangVerdict.WorkingSilently);
    }

    [Test]
    public void SilentAndIdle_UnderLimit_IsQuiet()
    {
        var (watch, now) = Run(5, cpuShare: 0.01);
        watch.Judge(now, T0, QuietAfter).ShouldBe(HangVerdict.Quiet);
    }

    [Test]
    public void SilentAndIdle_PastLimit_IsHung()
    {
        var (watch, now) = Run(10.5, cpuShare: 0.01);
        watch.Judge(now, T0, QuietAfter).ShouldBe(HangVerdict.Hung);
    }

    [Test]
    public void WorkStops_HangIsCountedFromLastWork()
    {
        var watch = new HangWatch(Limit);
        var t = TimeSpan.Zero;
        var cpu = TimeSpan.Zero;
        for (; t <= TimeSpan.FromMinutes(30); t += HangWatch.SampleEvery)
        {
            if (t <= TimeSpan.FromMinutes(20)) cpu = t; // 20 минут работы, потом процесс замер
            watch.Observe(T0 + t, new ProcessSample(cpu, 0, 8000, 8000));
            if (t == TimeSpan.FromMinutes(25)) watch.Judge(T0 + t, T0, QuietAfter).ShouldBe(HangVerdict.Quiet);
        }

        watch.Judge(T0 + TimeSpan.FromMinutes(30), T0, QuietAfter).ShouldBe(HangVerdict.Quiet);
        watch.Observe(T0 + TimeSpan.FromMinutes(32), new ProcessSample(cpu, 0, 8000, 8000));
        watch.Judge(T0 + TimeSpan.FromMinutes(32), T0, QuietAfter).ShouldBe(HangVerdict.Hung);
    }

    [Test]
    public void RareBlipsOfBackgroundThreads_DoNotKeepHungRevitAlive()
    {
        var watch = new HangWatch(Limit);
        var cpu = TimeSpan.Zero;
        var t = TimeSpan.Zero;
        for (; t <= TimeSpan.FromMinutes(11); t += HangWatch.SampleEvery)
        {
            if ((int)t.TotalSeconds % 60 == 0) cpu += TimeSpan.FromMilliseconds(300); // всплеск раз в минуту
            watch.Observe(T0 + t, new ProcessSample(cpu, 0, 8000, 8000));
        }

        watch.Judge(T0 + t - HangWatch.SampleEvery, T0, QuietAfter).ShouldBe(HangVerdict.Hung);
    }

    [Test]
    public void NoCounters_FallsBackToSilenceOnly()
    {
        var watch = new HangWatch(Limit);
        watch.Observe(T0 + TimeSpan.FromMinutes(11), null);

        watch.Judge(T0 + TimeSpan.FromMinutes(11), T0, QuietAfter).ShouldBe(HangVerdict.Hung);
        watch.DescribeActivity().ShouldBe("счётчики процесса недоступны");
    }

    [Test]
    public void TracksPeakMemory()
    {
        var watch = new HangWatch(Limit);
        watch.Observe(T0, new ProcessSample(TimeSpan.Zero, 0, 3000, 3500));
        watch.Observe(T0 + TimeSpan.FromSeconds(5), new ProcessSample(TimeSpan.Zero, 0, 11000, 12000));
        watch.Observe(T0 + TimeSpan.FromSeconds(10), new ProcessSample(TimeSpan.Zero, 0, 4000, 4500));

        watch.PeakWorkingSetMb.ShouldBe(11000);
        watch.PeakPrivateMb.ShouldBe(12000);
    }
}
