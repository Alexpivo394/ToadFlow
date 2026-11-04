using System.Net;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;
using ToadFlow.Server.Clash.Engine;
using ToadFlow.Server.Tests.Infrastructure;
using static ToadFlow.Server.Tests.Infrastructure.ClashDraftFixtures;

namespace ToadFlow.Server.Tests;

public class HealthTests : ServerTest
{
    [Test]
    public async Task Health_ReturnsOk()
    {
        var response = await Server.CreateClient().GetAsync("/api/health");

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        (body.GetProperty("status").GetString(), body.GetProperty("db").GetString()).ShouldBe(("ok", "ok"));
        body.GetProperty("clashVolumeFreeGb").GetDouble().ShouldBeGreaterThan(0);
    }

    [Test]
    public async Task Health_DbDown_503()
    {
        // База на порту, где никого нет; фоновые службы (миграции) не запускаются — сервер поднимается без базы.
        await using var server = new ServerFactory("Host=127.0.0.1;Port=1;Database=none;Username=x;Password=x;Timeout=2",
            services: s =>
            {
                foreach (var hosted in s.Where(d => d.ServiceType == typeof(IHostedService)).ToList()) s.Remove(hosted);
            });

        var response = await server.CreateClient().GetAsync("/api/health");

        response.StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);
        var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        (body.GetProperty("status").GetString(), body.GetProperty("db").GetString()).ShouldBe(("unhealthy", "unavailable"));
    }

    [Test]
    public async Task EngineStatus_AfterClaim_LastSeenSet()
    {
        var admin = await AdminAsync();
        var before = await admin.GetJsonAsync<EngineStatusView>("/api/clash/engine/status");
        (before.LastSeenAt, before.Queued).ShouldBe((null, 0));

        (await EngineClient(Server).PostAsync("/api/engine/runs/claim", null)).StatusCode.ShouldBe(HttpStatusCode.NoContent);

        var after = await admin.GetJsonAsync<EngineStatusView>("/api/clash/engine/status");
        after.LastSeenAt.ShouldNotBeNull().ShouldBeGreaterThanOrEqualTo(after.ServerStartedAt);
    }
}
