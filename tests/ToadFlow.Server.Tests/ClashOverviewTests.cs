using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Time.Testing;
using Shouldly;
using ToadFlow.Contracts;
using ToadFlow.Contracts.Clash;
using ToadFlow.Server.Clash;
using ToadFlow.Server.Clash.Collisions;
using ToadFlow.Server.Clash.Engine;
using ToadFlow.Server.Clash.Overview;
using ClashOverview = ToadFlow.Server.Clash.Overview.OverviewView;
using ToadFlow.Server.Data.Entities;
using ToadFlow.Server.Tests.Infrastructure;
using static ToadFlow.Server.Tests.Infrastructure.ClashDraftFixtures;

namespace ToadFlow.Server.Tests;

/// <summary>Обзор координатора: где проблема по всем проверкам. Пороги — в настройках модуля.</summary>
public class ClashOverviewTests : ServerTest
{
    private readonly FakeTimeProvider _time = new(DateTimeOffset.UtcNow);

    public ClashOverviewTests() => Time = _time;

    /// <summary>Утверждённая часть с одним удачным прогоном.</summary>
    private async Task<(HttpClient Admin, FakeAgent Agent, Guid PartId, Guid RunId)> CheckAsync(IReadOnlyList<EngineHit>? hits = null)
    {
        var admin = await AdminAsync();
        var agent = await FakeAgent.RegisterAsync(Server, admin);
        await agent.ConnectAsync("1.8.0", new InstalledRevitDto(2022, true));
        var partId = await PartAsync(Server, admin, agent, "POL_K1_AR.rvt", "POL_K1_OV.rvt");
        var engine = EngineClient(Server);
        (await admin.PostJsonAsync($"/api/clash/parts/{partId}/approve", new { comment = (string?)null, run = true })).EnsureSuccessStatusCode();
        var runId = (await (await engine.PostAsync("/api/engine/runs/claim", null)).ReadJsonAsync<EngineRunAssignment>()).RunId;
        (await PostApprovedResultAsync(engine, runId, 0, true, (hits ?? [Hit(1)]).Select(h => h with { FingerprintA = 1 }).ToList(), Stats, [], []))
            .EnsureSuccessStatusCode();
        return (admin, agent, partId, runId);
    }

    private static Task<ClashOverview> OverviewAsync(HttpClient client) => client.GetJsonAsync<ClashOverview>("/api/clash/overview");

    [Test]
    public async Task Overview_StaleSnapshot_NotUploaded()
    {
        var (admin, agent, partId, _) = await CheckAsync();
        await using var _ = agent;
        (await OverviewAsync(admin)).NotUploaded.ShouldBeEmpty();

        _time.Advance(TimeSpan.FromDays(8));
        await DbAsync(db => db.Set<EngineRun>().ExecuteUpdateAsync(r => r.SetProperty(x => x.FinishedAt, _time.GetUtcNow())));

        var stale = await OverviewAsync(admin);
        stale.NotUploaded.Count.ShouldBe(2);
        stale.NotUploaded.ShouldAllBe(m => m.PartId == partId && m.Reason == "выгрузка старше 7 дней");
        stale.NotUploaded.Select(m => m.ModelName).Order().ShouldBe(["POL_K1_AR.rvt", "POL_K1_OV.rvt"]);
    }

    [Test]
    public async Task Overview_ModelUncheckedInLastRun_NotUploaded()
    {
        var (admin, agent, partId, runId) = await CheckAsync();
        await using var _ = agent;
        var model = await DbAsync(db => db.Set<ClashModel>().SingleAsync(m => m.Name == "POL_K1_OV.rvt"));
        await DbAsync(db => db.Set<EngineRun>().Where(r => r.Id == runId).ExecuteUpdateAsync(r => r.SetProperty(x => x.UncheckedModels, new[] { model.Id })));

        var item = (await OverviewAsync(admin)).NotUploaded.ShouldHaveSingleItem();

        (item.PartId, item.ModelId, item.ModelName, item.Reason).ShouldBe((partId, model.Id, "POL_K1_OV.rvt", "не выгрузилась в последнем цикле"));
    }

    [Test]
    public async Task Overview_LastRunFailed_RunFailed()
    {
        var (admin, agent, partId, _) = await CheckAsync();
        await using var _ = agent;
        _time.Advance(TimeSpan.FromMinutes(1));
        (await admin.PostAsync($"/api/clash/parts/{partId}/approved/run", null)).EnsureSuccessStatusCode();
        await DbAsync(db => db.Set<EngineRun>().Where(r => r.State == EngineRunState.Queued)
            .ExecuteUpdateAsync(r => r.SetProperty(x => x.State, EngineRunState.Failed).SetProperty(x => x.Error, "нет памяти")));

        var item = (await OverviewAsync(admin)).RunFailed.ShouldHaveSingleItem();

        (item.PartId, item.Reason).ShouldBe((partId, "прогон упал: нет памяти"));
        _time.Advance(TimeSpan.FromDays(9));
        var late = (await OverviewAsync(admin)).NoRecentRun.ShouldHaveSingleItem();
        (late.PartId, late.Count, late.Reason).ShouldBe((partId, 9, "не прогонялась 9 дней"));
    }

    /// <summary>Всплеск — новых больше 10 % от активных и больше 50 штук.</summary>
    [Test]
    [Arguments(60, false)]
    [Arguments(120, true)]
    public async Task Overview_Spike_BothThresholds(int added, bool spike)
    {
        var (admin, agent, partId, runId) = await CheckAsync();
        await using var _ = agent;
        await DbAsync(db => db.Set<EngineRun>().Where(r => r.Id == runId).ExecuteUpdateAsync(r => r
            .SetProperty(x => x.Summary, new ApprovedRunSummary(1000 + added, added, 1000, 0, 0, 0, 0, 0, 0))
            .SetProperty(x => x.PreviousRunId, Guid.NewGuid())));

        var spikes = (await OverviewAsync(admin)).Spike;

        if (!spike) spikes.ShouldBeEmpty();
        else (spikes.ShouldHaveSingleItem().PartId, spikes[0].Count, spikes[0].Reason).ShouldBe((partId, added, $"+{added} новых"));
    }

    [Test]
    public async Task Overview_Stuck_NoFeedFor30Days()
    {
        var (admin, agent, partId, _) = await CheckAsync([Hit(1), Hit(2), Hit(3)]);
        await using var _ = agent;
        _time.Advance(TimeSpan.FromDays(31));
        var two = await DbAsync(db => db.Set<ClashCollision>().SingleAsync(c => c.Number == 2));
        (await admin.PostJsonAsync($"/api/clash/collisions/{two.Id}/notes", new { text = "Смотрим" })).EnsureSuccessStatusCode();
        await DbAsync(db => db.Set<ClashCollision>().Where(c => c.Number == 3).ExecuteUpdateAsync(c => c.SetProperty(x => x.Status, CollisionStatus.Fake)));

        var stuck = (await OverviewAsync(admin)).Stuck.ShouldHaveSingleItem();

        (stuck.PartId, stuck.Count, stuck.Reason).ShouldBe((partId, 1, "висят 1"));
        var list = await admin.GetJsonAsync<CollisionPage>($"/api/clash/parts/{partId}/approved/collisions?stuckDays=30");
        list.Items.ShouldHaveSingleItem().Number.ShouldBe(1);
    }

    [Test]
    public async Task Overview_Viewer403()
    {
        (await (await ClientAsync(UserRole.Viewer)).GetAsync("/api/clash/overview")).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Test]
    public async Task Settings_OverviewValidated()
    {
        var admin = await AdminAsync();
        var current = await admin.GetJsonAsync<ClashSettingsView>("/api/clash/settings");
        current.Overview.ShouldBe(new ClashOverviewSettings());

        (await admin.PutJsonAsync("/api/clash/settings", new { storage = current.Storage, overview = new ClashOverviewSettings(StaleDays: 0) }))
            .StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await admin.PutJsonAsync("/api/clash/settings", new { storage = current.Storage, overview = new ClashOverviewSettings(SpikeShare: 1.5) }))
            .StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await admin.PutJsonAsync("/api/clash/settings", new { storage = current.Storage, overview = new ClashOverviewSettings(StuckDays: 14) }))
            .EnsureSuccessStatusCode();

        (await admin.GetJsonAsync<ClashSettingsView>("/api/clash/settings")).Overview.StuckDays.ShouldBe(14);
        (await admin.PutJsonAsync("/api/clash/settings", new { storage = current.Storage, overview = new ClashOverviewSettings(AgentsOfflineMinutes: 0) }))
            .StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    /// <summary>Расписание выгрузки для коллизий с частями <paramref name="parts" />.</summary>
    private Task ScheduleAsync(Guid[] parts, bool enabled) => DbAsync(async db =>
    {
        var schedule = Seed.Schedule(db, Seed.Source(db).Id);
        schedule.ModuleKind = ModuleKinds.ClashExtract;
        schedule.IsEnabled = enabled;
        schedule.ModuleSettingsJson = System.Text.Json.JsonSerializer.Serialize(new ClashScheduleSettings("Navisworks", "", parts), ToadFlow.Server.Data.ToadFlowDb.Json);
        await db.SaveChangesAsync();
    });

    [Test]
    public async Task Overview_ApprovedCheckWithoutSchedule_Warning()
    {
        var (admin, agent, partId, _) = await CheckAsync();
        await using var _ = agent;

        var item = (await OverviewAsync(admin)).NoSchedule.ShouldHaveSingleItem();
        (item.PartId, item.Reason).ShouldBe((partId, "нет регулярного прогона"));

        await ScheduleAsync([partId], enabled: false);
        (await OverviewAsync(admin)).NoSchedule.ShouldHaveSingleItem(); // выключенное расписание не прогоняет

        await ScheduleAsync([partId], enabled: true);
        (await OverviewAsync(admin)).NoSchedule.ShouldBeEmpty();
    }

    [Test]
    public async Task Overview_AgentsOffline_Warning()
    {
        var (admin, agent, _, _) = await CheckAsync();
        await using var _ = agent;
        (await OverviewAsync(admin)).AgentsOffline.ShouldBeNull();

        var seen = _time.GetUtcNow();
        await DbAsync(db => db.Agents.ExecuteUpdateAsync(a => a.SetProperty(x => x.State, AgentState.Offline).SetProperty(x => x.LastSeenAt, seen)));
        _time.Advance(TimeSpan.FromMinutes(5));
        (await OverviewAsync(admin)).AgentsOffline.ShouldBeNull(); // меньше порога 10 мин

        _time.Advance(TimeSpan.FromMinutes(20));
        var offline = (await OverviewAsync(admin)).AgentsOffline.ShouldNotBeNull();
        (offline.Minutes, offline.Reason).ShouldBe((25, "Агенты не в сети 25 мин"));
    }
}
