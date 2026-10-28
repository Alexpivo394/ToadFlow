using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR.Client;
using Shouldly;
using ToadFlow.Contracts;
using ToadFlow.Server.Tests.Infrastructure;
using static ToadFlow.Server.Tests.Infrastructure.ClashDraftFixtures;

namespace ToadFlow.Server.Tests;

public class ClashDraftEventsTests : ServerTest
{
    [Test]
    public async Task DraftRunEvents_PublishedOnStateChange()
    {
        var admin = await AdminAsync();
        await using var agent = await FakeAgent.RegisterAsync(Server, admin);
        await agent.ConnectAsync("1.8.0", new InstalledRevitDto(2022, true));
        var partId = await PartAsync(Server, admin, agent, "POL_K1_AR.rvt");
        var token = await TokenAsync(ServerFactory.AdminLogin, ServerFactory.AdminPassword);
        await using var hub = new HubConnectionBuilder()
            .WithUrl(new Uri(Server.Server.BaseAddress, "/hubs/ui"), options =>
            {
                options.Transports = HttpTransportType.LongPolling;
                options.HttpMessageHandlerFactory = _ => Server.Server.CreateHandler();
                options.AccessTokenProvider = () => Task.FromResult<string?>(token);
            })
            .Build();
        var received = new TaskCompletionSource<string>();
        hub.On<string, string>("changed", (entity, id) =>
        {
            if (entity == "clash-parts" && id == partId.ToString()) received.TrySetResult(id);
        });
        await hub.StartAsync();

        // Подключение попадает в рассылку чуть позже рукопожатия (см. UiHub_ReceivesChanged): «Прогнать», пока событие не дойдёт.
        for (var attempt = 0; attempt < 30 && !received.Task.IsCompleted; attempt++)
        {
            (await admin.PostAsync($"/api/clash/parts/{partId}/draft/run", null)).EnsureSuccessStatusCode();
            await Task.WhenAny(received.Task, Task.Delay(300));
        }

        (await received.Task.WaitAsync(TimeSpan.FromSeconds(5))).ShouldBe(partId.ToString());
    }
}
