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

    /// <summary>Compose больше не требует ключ: на рабочей установке с включёнными коллизиями его требует сервер.</summary>
    [Test]
    public async Task EngineKey_EmptyInProduction_ClashEnabled_HostFailsToStart()
    {
        await using var noKey = Server.WithWebHostBuilder(builder => builder.UseEnvironment("Production").UseSetting("Clash:EngineKey", ""));

        var error = Should.Throw<OptionsValidationException>(() => noKey.CreateClient());

        error.Message.ShouldContain("CLASH_ENGINE_KEY");
        error.Message.ShouldContain("MODULES");
    }

    [Test]
    public async Task EngineKey_EmptyInProduction_ClashDisabled_HostStarts()
    {
        await using var noClash = Server.WithWebHostBuilder(builder =>
            builder.UseEnvironment("Production").UseSetting("Clash:EngineKey", "").UseSetting("Modules", "export,loi"));
        using var client = noClash.CreateClient();

        (await client.GetAsync("/api/health")).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Test]
    public async Task EngineKey_Length31_HostFailsToStart()
    {
        await using var shortKey = Server.WithWebHostBuilder(builder => builder.UseSetting("Clash:EngineKey", new string('k', ClashEngineOptions.MinKeyLength - 1)));

        Should.Throw<OptionsValidationException>(() => shortKey.CreateClient()).Message.ShouldContain("CLASH_ENGINE_KEY");
    }

    [Test]
    public async Task EngineKey_Length32_HostStarts()
    {
        var key = new string('k', ClashEngineOptions.MinKeyLength);
        await using var exact = Server.WithWebHostBuilder(builder => builder.UseSetting("Clash:EngineKey", key));
        using var client = exact.CreateClient();
        client.DefaultRequestHeaders.Add(EngineApi.KeyHeader, key);

        var response = await client.PostAsync("/api/engine/runs/claim", null);

        response.StatusCode.ShouldNotBe(HttpStatusCode.Unauthorized);
    }
}
