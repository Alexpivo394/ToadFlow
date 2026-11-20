using System.Net;
using System.Text;
using Shouldly;
using ToadFlow.Server.Tests.Infrastructure;

namespace ToadFlow.Clash.Worker.Tests;

public class ModuleGateTests : ServerTest
{
    [Test]
    public async Task ClashEnabled_True()
    {
        using var http = new HttpClient(Server.Server.CreateHandler()) { BaseAddress = new Uri("http://localhost/") };

        (await ModuleGate.IsClashEnabledAsync(http, CancellationToken.None)).ShouldBe(true);
    }

    [Test]
    public async Task ServerDown_Null()
    {
        using var http = new HttpClient { BaseAddress = new Uri("http://127.0.0.1:1/"), Timeout = TimeSpan.FromSeconds(5) };

        (await ModuleGate.IsClashEnabledAsync(http, CancellationToken.None)).ShouldBeNull();
    }

    [Test]
    public async Task OldServerWithoutField_True()
    {
        using var http = new HttpClient(new Fixed("""{"status":"ok","db":"ok"}""")) { BaseAddress = new Uri("http://localhost/") };

        (await ModuleGate.IsClashEnabledAsync(http, CancellationToken.None)).ShouldBe(true);
    }

    private sealed class Fixed(string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") });
    }
}

public class ModuleGateDisabledTests : ServerTest
{
    public ModuleGateDisabledTests() => Modules = "export";

    [Test]
    public async Task ClashDisabled_False()
    {
        using var http = new HttpClient(Server.Server.CreateHandler()) { BaseAddress = new Uri("http://localhost/") };

        (await ModuleGate.IsClashEnabledAsync(http, CancellationToken.None)).ShouldBe(false);
    }
}
