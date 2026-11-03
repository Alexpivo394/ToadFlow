using System.Collections.Concurrent;
using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using ToadFlow.Contracts;
using ToadFlow.Contracts.Clash;
using ToadFlow.Server.Clash.Engine;
using ToadFlow.Server.Events;
using ToadFlow.Server.Tests.Infrastructure;
using static ToadFlow.Server.Tests.Infrastructure.ClashDraftFixtures;

namespace ToadFlow.Server.Tests;

/// <summary>Ход движка — своё событие: веб перечитывает только состояние прогона, а не списки коллизий и черновик.</summary>
public class RunProgressEventTests : ServerTest
{
    private sealed class Recorder : IDomainEventHandler
    {
        public static readonly ConcurrentQueue<DomainEvent> Seen = new();

        public Task HandleAsync(DomainEvent domainEvent, CancellationToken ct)
        {
            Seen.Enqueue(domainEvent);
            return Task.CompletedTask;
        }
    }

    public RunProgressEventTests() => Services = services => services.AddScoped<IDomainEventHandler, Recorder>();

    [Test]
    public async Task EngineProgress_PublishesRunProgressNotDraftChanged()
    {
        var admin = await AdminAsync();
        await using var agent = await FakeAgent.RegisterAsync(Server, admin);
        await agent.ConnectAsync("1.8.0", new InstalledRevitDto(2022, true));
        var partId = await PartAsync(Server, admin, agent, "POL_K1_AR.rvt");
        var engine = EngineClient(Server);
        (await admin.PostAsync($"/api/clash/parts/{partId}/draft/run", null)).EnsureSuccessStatusCode();
        var run = await (await engine.PostAsync("/api/engine/runs/claim", null)).ReadJsonAsync<EngineRunAssignment>();
        await EventuallyAsync(() => Task.FromResult(Recorder.Seen.Any(e => e is ClashDraftChanged d && d.PartId == partId)));
        while (Recorder.Seen.TryDequeue(out _)) { }

        (await engine.PostJsonAsync($"/api/engine/runs/{run.RunId}/progress", new EngineProgress(run.RunId, "Поиск пересечений", "", run.Attempt)))
            .StatusCode.ShouldBe(HttpStatusCode.OK);

        await EventuallyAsync(() => Task.FromResult(Recorder.Seen.Any(e => e is ClashRunProgress p && p.PartId == partId)));
        Recorder.Seen.OfType<ClashDraftChanged>().ShouldBeEmpty();
        Recorder.Seen.OfType<ClashRunProgress>().Single().UiEntity.ShouldBe("clash-run-progress");
    }
}
