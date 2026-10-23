using System.Net;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Time.Testing;
using Shouldly;
using ToadFlow.Contracts;
using ToadFlow.Server.Tests.Infrastructure;

namespace ToadFlow.Server.Tests;

public sealed record AgentView(
    Guid Id,
    string MachineName,
    string AgentVersion,
    AgentState State,
    bool IsDraining,
    DateTimeOffset? LastSeenAt,
    InstalledRevitDto[] Revits,
    VersionLimitDto[] Limits,
    InstanceStatusDto[] Instances,
    bool IsOutdated);

public class AgentHubTests : ServerTest
{
    private readonly FakeTimeProvider _time = new(DateTimeOffset.UtcNow); // от настоящего «сейчас»: токены JWT проверяются по настоящим часам

    public AgentHubTests() => Time = _time;

    [Test]
    public async Task Hub_WrongKey_Rejected()
    {
        await using var agent = await FakeAgent.RegisterAsync(Server, await AdminAsync());
        await using var impostor = agent.NewConnection("wrong-key");

        await Should.ThrowAsync<HttpRequestException>(() => impostor.StartAsync());
    }

    [Test]
    public async Task Hello_SetsOnlineAndDefaultLimits()
    {
        await using var agent = await FakeAgent.RegisterAsync(Server, await AdminAsync());

        var settings = await agent.ConnectAsync(new InstalledRevitDto(2022, true), new InstalledRevitDto(2024, false));

        settings.Limits.ShouldBe([new VersionLimitDto(2022, 1), new VersionLimitDto(2024, 1)]);
        settings.SilenceTimeoutMinutes.ShouldBe(10);
        var view = (await (await ClientAsync(UserRole.Viewer)).GetJsonAsync<AgentView[]>("/api/agents")).ShouldHaveSingleItem();
        view.State.ShouldBe(AgentState.Online);
        view.Revits.Length.ShouldBe(2);
    }

    [Test]
    public async Task Hello_FourVersions_DefaultLimitsCappedAt3()
    {
        await using var agent = await FakeAgent.RegisterAsync(Server, await AdminAsync());

        var settings = await agent.ConnectAsync(new InstalledRevitDto(2020, true), new InstalledRevitDto(2022, true),
            new InstalledRevitDto(2024, true), new InstalledRevitDto(2026, true));

        settings.Limits.Sum(l => l.MaxInstances).ShouldBe(3);
        settings.Limits.Length.ShouldBe(4);
    }

    [Test]
    public async Task Limits_SumAbove3_400()
    {
        var admin = await AdminAsync();
        await using var agent = await FakeAgent.RegisterAsync(Server, admin);
        await agent.ConnectAsync(new InstalledRevitDto(2022, true), new InstalledRevitDto(2024, true));

        var response = await admin.PutJsonAsync($"/api/agents/{agent.Id}/limits", new[] { new VersionLimitDto(2022, 2), new VersionLimitDto(2024, 2) });

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync()).ShouldContain("3");
    }

    [Test]
    public async Task Limits_UnknownYear_400()
    {
        var admin = await AdminAsync();
        await using var agent = await FakeAgent.RegisterAsync(Server, admin);
        await agent.ConnectAsync(new InstalledRevitDto(2022, true));

        (await admin.PutJsonAsync($"/api/agents/{agent.Id}/limits", new[] { new VersionLimitDto(2025, 1) }))
            .StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Test]
    public async Task Limits_Saved_PushedToAgent()
    {
        var admin = await AdminAsync();
        await using var agent = await FakeAgent.RegisterAsync(Server, admin);
        await agent.ConnectAsync(new InstalledRevitDto(2022, true), new InstalledRevitDto(2024, true));

        (await admin.PutJsonAsync($"/api/agents/{agent.Id}/limits", new[] { new VersionLimitDto(2022, 1), new VersionLimitDto(2024, 2) }))
            .EnsureSuccessStatusCode();

        await EventuallyAsync(() => Task.FromResult(agent.AppliedSettings.Any(s => s.Limits.Any(l => l is { Year: 2024, MaxInstances: 2 }))));
    }

    [Test]
    public async Task Agent_GoesOffline_AfterMissedHeartbeats()
    {
        var viewer = await ClientAsync(UserRole.Viewer);
        await using var agent = await FakeAgent.RegisterAsync(Server, await AdminAsync());
        await agent.ConnectAsync();

        _time.Advance(TimeSpan.FromSeconds(45));

        await EventuallyAsync(async () => (await viewer.GetJsonAsync<AgentView[]>("/api/agents")).Single().State == AgentState.Offline);
        await EventuallyAsync(async () => (await DbAsync(db => db.SystemEvents.ToListAsync())).Any(e => e.Text.Contains("потерял связь")));
    }

    [Test]
    public async Task Agent_StaysOnline_WithHeartbeats()
    {
        var viewer = await ClientAsync(UserRole.Viewer);
        await using var agent = await FakeAgent.RegisterAsync(Server, await AdminAsync());
        await agent.ConnectAsync();

        for (var i = 0; i < 5; i++)
        {
            _time.Advance(TimeSpan.FromSeconds(10));
            await agent.HeartbeatAsync();
        }

        await Task.Delay(300);
        (await viewer.GetJsonAsync<AgentView[]>("/api/agents")).Single().State.ShouldBe(AgentState.Online);
    }

    [Test]
    public async Task Agent_GoesOffline_OnDisconnect()
    {
        var viewer = await ClientAsync(UserRole.Viewer);
        await using var agent = await FakeAgent.RegisterAsync(Server, await AdminAsync());
        await agent.ConnectAsync();

        await agent.Hub.StopAsync();

        await EventuallyAsync(async () => (await viewer.GetJsonAsync<AgentView[]>("/api/agents")).Single().State == AgentState.Offline);
    }

    [Test]
    public async Task Heartbeat_ShowsInstancesInAgentList()
    {
        var viewer = await ClientAsync(UserRole.Viewer);
        await using var agent = await FakeAgent.RegisterAsync(Server, await AdminAsync());
        await agent.ConnectAsync();

        await agent.HeartbeatAsync(new InstanceStatusDto(1, 2022, 11840, InstanceState.Busy, Guid.NewGuid(), "Экспорт NWC", _time.GetUtcNow()));

        var instance = (await viewer.GetJsonAsync<AgentView[]>("/api/agents")).Single().Instances.ShouldHaveSingleItem();
        instance.Pid.ShouldBe(11840);
        instance.Stage.ShouldBe("Экспорт NWC");
    }

    [Test]
    public async Task Drain_SetsStateAndPushesSettings()
    {
        var admin = await AdminAsync();
        await using var agent = await FakeAgent.RegisterAsync(Server, admin);
        await agent.ConnectAsync();

        (await admin.PostJsonAsync($"/api/agents/{agent.Id}/drain", new { draining = true })).EnsureSuccessStatusCode();

        (await admin.GetJsonAsync<AgentView[]>("/api/agents")).Single().State.ShouldBe(AgentState.Draining);
        await EventuallyAsync(() => Task.FromResult(agent.AppliedSettings.Any(s => s.Draining)));
    }

    [Test]
    public async Task Disable_RejectsFurtherConnections()
    {
        var admin = await AdminAsync();
        await using var agent = await FakeAgent.RegisterAsync(Server, admin);
        await agent.ConnectAsync();

        (await admin.PostAsync($"/api/agents/{agent.Id}/disable", null)).EnsureSuccessStatusCode();

        await using var again = agent.NewConnection();
        await Should.ThrowAsync<HttpRequestException>(() => again.StartAsync());
        (await admin.GetJsonAsync<AgentView[]>("/api/agents")).Single().State.ShouldBe(AgentState.Disabled);
    }

    [Test]
    public async Task Delete_OnlineAgent_409()
    {
        var admin = await AdminAsync();
        await using var agent = await FakeAgent.RegisterAsync(Server, admin);
        await agent.ConnectAsync();

        (await admin.DeleteAsync($"/api/agents/{agent.Id}")).StatusCode.ShouldBe(HttpStatusCode.Conflict);
    }

    [Test]
    public async Task OlderAgentVersion_IsOutdated()
    {
        var admin = await AdminAsync();
        await using var fresh = await FakeAgent.RegisterAsync(Server, admin, "FARM-01");
        await using var old = await FakeAgent.RegisterAsync(Server, admin, "FARM-02");
        await fresh.Hub.StartAsync();
        await fresh.Hub.InvokeAsync<AgentSettingsDto>("Hello", new AgentHelloDto("FARM-01", "1.0.3", 65536, [new InstalledRevitDto(2022, true)]));
        await old.Hub.StartAsync();
        await old.Hub.InvokeAsync<AgentSettingsDto>("Hello", new AgentHelloDto("FARM-02", "1.0.1", 32768, [new InstalledRevitDto(2022, true)]));

        var agents = await admin.GetJsonAsync<AgentView[]>("/api/agents");

        agents.Single(a => a.MachineName == "FARM-02").IsOutdated.ShouldBeTrue();
        agents.Single(a => a.MachineName == "FARM-01").IsOutdated.ShouldBeFalse();
    }
}

public class AgentAutoKillTests : ServerTest
{
    [Test]
    public async Task ReportInstanceKilled_WritesSystemEvent()
    {
        await using var agent = await FakeAgent.RegisterAsync(Server, await AdminAsync(), "FARM-01");
        await agent.ConnectAsync();

        await agent.ReportInstanceKilledAsync(1, "не отвечал 10 мин");

        await EventuallyAsync(async () =>
            (await DbAsync(db => db.SystemEvents.ToListAsync())).Any(e => e.Text == "Revit #1 на FARM-01 убит: не отвечал 10 мин" && e.ActorName == "FARM-01"));
    }
}
