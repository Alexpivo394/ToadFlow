using System.Security.Claims;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using Shouldly;
using ToadFlow.Contracts;
using ToadFlow.Server.Agents;
using ToadFlow.Server.Data;
using ToadFlow.Server.Overview;
using ToadFlow.Server.Tests.Infrastructure;
using OverviewDto = ToadFlow.Server.Overview.OverviewView;

namespace ToadFlow.Server.Tests;

/// <summary>Пульс «Обзора»: задания по часам за сутки, очередь, занятые Revit.</summary>
public class OverviewPulseTests : ServerTest
{
    private static readonly DateTimeOffset Now = new(2026, 11, 21, 10, 30, 0, TimeSpan.Zero);
    private readonly FakeTimeProvider _time = new(Now);

    public OverviewPulseTests()
    {
        Time = _time;
        Services = services =>
        {
            OverviewFakes.Quiet(services);
            OverviewFakes.TokensOnTestClock(services);
        };
    }

    private async Task<OverviewPulse> PulseAsync() => (await (await AdminAsync()).GetJsonAsync<OverviewDto>("/api/overview")).Pulse;

    private static void Finished(ToadFlowDb db, JobStatus status, DateTimeOffset at, string kind = ModuleKinds.Export)
    {
        var job = Seed.Job(db, Seed.Run(db, RunState.Completed).Id);
        job.ModuleKind = kind;
        job.Status = status;
        job.FinishedAt = at;
    }

    [Test]
    public async Task Pulse_EmptyBase_AllZero()
    {
        var pulse = await PulseAsync();

        pulse.Hourly.Count.ShouldBe(24);
        pulse.Hourly.ShouldAllBe(h => h.Succeeded == 0 && h.Warning == 0 && h.Failed == 0);
        (pulse.DoneDay, pulse.FailedDay, pulse.Queued, pulse.RevitBusy, pulse.RevitTotal).ShouldBe((0, 0, 0, 0, 0));
    }

    [Test]
    public async Task Pulse_JobsBucketedByHour()
    {
        await DbAsync(async db =>
        {
            Finished(db, JobStatus.Succeeded, Now.AddMinutes(-25)); // 10:05
            Finished(db, JobStatus.Warning, Now.AddMinutes(-31)); // 09:59
            Finished(db, JobStatus.Failed, Now.AddMinutes(-80)); // 09:10
            Finished(db, JobStatus.Succeeded, Now.AddHours(-25));
            Finished(db, JobStatus.Cancelled, Now.AddMinutes(-20));
            await db.SaveChangesAsync();
        });

        var pulse = await PulseAsync();

        pulse.Hourly[23].ShouldBe(new HourPoint(new DateTimeOffset(2026, 11, 21, 10, 0, 0, TimeSpan.Zero), 1, 0, 0));
        pulse.Hourly[22].ShouldBe(new HourPoint(new DateTimeOffset(2026, 11, 21, 9, 0, 0, TimeSpan.Zero), 0, 1, 1));
        (pulse.DoneDay, pulse.FailedDay).ShouldBe((2, 1));
    }

    [Test]
    public async Task Pulse_QueuedCounted()
    {
        await DbAsync(async db =>
        {
            var run = Seed.Run(db);
            for (var i = 0; i < 3; i++) Seed.Job(db, run.Id);
            await db.SaveChangesAsync();
        });

        (await PulseAsync()).Queued.ShouldBe(3);
    }

    [Test]
    public async Task Pulse_RevitOfOfflineAgent_NotCounted()
    {
        var registry = Server.Services.GetRequiredService<AgentRegistry>();
        var online = Guid.CreateVersion7();
        registry.Connected(online, new FakeCaller("c1"));
        registry.Beat(online,
        [
            new InstanceStatusDto(1, 2022, 100, InstanceState.Busy, Guid.NewGuid(), "Выгрузка", Now),
            new InstanceStatusDto(2, 2024, 101, InstanceState.Idle, null, null, null)
        ]);

        var pulse = await PulseAsync();

        (pulse.RevitBusy, pulse.RevitTotal).ShouldBe((1, 2));
    }

    private sealed class FakeCaller(string id) : HubCallerContext
    {
        public override string ConnectionId => id;
        public override string? UserIdentifier => null;
        public override ClaimsPrincipal? User => null;
        public override IDictionary<object, object?> Items { get; } = new Dictionary<object, object?>();
        public override IFeatureCollection Features { get; } = new FeatureCollection();
        public override CancellationToken ConnectionAborted => CancellationToken.None;
        public override void Abort() { }
    }
}

public class OverviewPulseDisabledModuleTests : ServerTest
{
    public OverviewPulseDisabledModuleTests()
    {
        Modules = "export";
        Services = services =>
        {
            OverviewFakes.Quiet(services);
            OverviewFakes.TokensOnTestClock(services);
        };
    }

    [Test]
    public async Task Pulse_JobsOfDisabledModule_Ignored()
    {
        await DbAsync(async db =>
        {
            var job = Seed.Job(db, Seed.Run(db, RunState.Completed).Id);
            job.ModuleKind = ModuleKinds.Loi;
            job.Status = JobStatus.Succeeded;
            job.FinishedAt = DateTimeOffset.UtcNow.AddMinutes(-5);
            await db.SaveChangesAsync();
        });

        var pulse = (await (await AdminAsync()).GetJsonAsync<OverviewDto>("/api/overview")).Pulse;

        pulse.DoneDay.ShouldBe(0);
    }
}
