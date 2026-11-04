using Microsoft.EntityFrameworkCore;
using Shouldly;
using ToadFlow.Contracts;
using ToadFlow.Contracts.Clash;
using ToadFlow.Server.Clash.Engine;
using ToadFlow.Server.Tests.Infrastructure;
using static ToadFlow.Server.Tests.Infrastructure.ClashDraftFixtures;

namespace ToadFlow.Server.Tests;

/// <summary>Прогон в очереди знает своё место и обычную длительность прогонов части.</summary>
public class RunQueueTests : ServerTest
{
    [Test]
    public async Task Run_Queued_PositionAndTypical()
    {
        var admin = await AdminAsync();
        await using var agent = await FakeAgent.RegisterAsync(Server, admin);
        await agent.ConnectAsync("1.8.0", new InstalledRevitDto(2022, true));
        var first = await PartAsync(Server, admin, agent, "POL_K1_AR.rvt");
        var second = await PartAsync(Server, admin, agent, "POL_K1_AR.rvt");
        var third = await PartAsync(Server, admin, agent, "POL_K1_AR.rvt");
        var engine = EngineClient(Server);

        // Прошлые удачные прогоны третьей части — 60 и 120 с.
        foreach (var seconds in new[] { 60, 120 })
        {
            (await admin.PostAsync($"/api/clash/parts/{third}/draft/run", null)).EnsureSuccessStatusCode();
            var past = await (await engine.PostAsync("/api/engine/runs/claim", null)).ReadJsonAsync<EngineRunAssignment>();
            var finished = DateTimeOffset.UtcNow.AddMinutes(-10);
            await DbAsync(db => db.Set<EngineRun>().Where(r => r.Id == past.RunId).ExecuteUpdateAsync(r => r
                .SetProperty(x => x.State, EngineRunState.Succeeded).SetProperty(x => x.LeaseUntil, (DateTimeOffset?)null)
                .SetProperty(x => x.StartedAt, finished.AddSeconds(-seconds)).SetProperty(x => x.FinishedAt, finished)));
        }

        (await admin.PostAsync($"/api/clash/parts/{first}/draft/run", null)).EnsureSuccessStatusCode();
        (await engine.PostAsync("/api/engine/runs/claim", null)).EnsureSuccessStatusCode(); // первая выполняется
        (await admin.PostAsync($"/api/clash/parts/{second}/draft/run", null)).EnsureSuccessStatusCode();
        (await admin.PostAsync($"/api/clash/parts/{third}/draft/run", null)).EnsureSuccessStatusCode();

        var running = await admin.GetJsonAsync<DraftRunSummary>($"/api/clash/parts/{first}/draft/run");
        var queuedFirst = await admin.GetJsonAsync<DraftRunSummary>($"/api/clash/parts/{second}/draft/run");
        var queuedSecond = await admin.GetJsonAsync<DraftRunSummary>($"/api/clash/parts/{third}/draft/run");

        (running.State, running.QueuePosition).ShouldBe((EngineRunState.Running, (int?)null));
        (queuedFirst.QueuePosition, queuedFirst.TypicalSeconds).ShouldBe((1, (double?)null)); // прогонов части ещё не было
        (queuedSecond.QueuePosition, queuedSecond.TypicalSeconds).ShouldBe((2, 90.0));
    }
}
