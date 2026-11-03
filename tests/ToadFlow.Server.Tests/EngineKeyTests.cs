using System.Net;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Options;
using Shouldly;
using ToadFlow.Contracts.Clash;
using ToadFlow.Server.Clash.Engine;
using ToadFlow.Server.Tests.Infrastructure;

namespace ToadFlow.Server.Tests;

public class EngineKeyTests : ServerTest
{
    [Test]
    public async Task EngineKey_Shorter32_HostFailsToStart()
    {
        await using var shortKey = Server.WithWebHostBuilder(builder => builder.UseSetting("Clash:EngineKey", new string('k', 20)));

        var error = Should.Throw<OptionsValidationException>(() => shortKey.CreateClient());

        error.Message.ShouldContain("CLASH_ENGINE_KEY");
        error.Message.ShouldContain(ClashEngineOptions.MinKeyLength.ToString());
    }

    [Test]
    public async Task EngineKey_Empty_EngineDisabledNotCrash()
    {
        await using var noKey = Server.WithWebHostBuilder(builder => builder.UseSetting("Clash:EngineKey", ""));
        using var client = noKey.CreateClient();
        client.DefaultRequestHeaders.Add(EngineApi.KeyHeader, new string('k', 40));

        var response = await client.PostAsync("/api/engine/runs/claim", null);

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }
}
