using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using ToadFlow.Contracts;
using ToadFlow.Server.Data.Entities;
using ToadFlow.Server.Events;
using ToadFlow.Server.Tests.Infrastructure;

namespace ToadFlow.Server.Tests;

public class EventsTests : ServerTest
{
    private sealed record EventRow(long Id, DateTimeOffset At, ActorKind ActorKind, string ActorName, string Kind, string Text);

    [Test]
    public async Task Publish_ScheduleChanged_WritesSystemEvent()
    {
        var viewer = await ClientAsync(UserRole.Viewer);
        var events = Server.Services.GetRequiredService<IDomainEvents>();

        events.Publish(new ScheduleChanged(new Actor(ActorKind.User, "Иван Петров"), Guid.NewGuid(), "Школа №5 — NWC", "повтор 6 ч → 4 ч"));

        EventRow[] rows = [];
        await EventuallyAsync(async () => (rows = await viewer.GetJsonAsync<EventRow[]>("/api/events")).Length > 0);
        var row = rows.ShouldHaveSingleItem();
        row.ActorName.ShouldBe("Иван Петров");
        row.Text.ShouldContain("Школа №5 — NWC");
        row.Text.ShouldContain("повтор 6 ч → 4 ч");
    }

    [Test]
    public async Task Publish_JobChanged_NotWrittenToSystemEvents()
    {
        var viewer = await ClientAsync(UserRole.Viewer);
        var events = Server.Services.GetRequiredService<IDomainEvents>();

        events.Publish(new JobChanged(Actor.System, Guid.NewGuid(), Guid.NewGuid()));
        events.Publish(new SettingsChanged(new Actor(ActorKind.User, "Админ"), "хранение логов 90 → 30 дней"));

        EventRow[] rows = [];
        await EventuallyAsync(async () => (rows = await viewer.GetJsonAsync<EventRow[]>("/api/events")).Length > 0);
        await Task.Delay(300);
        rows = await viewer.GetJsonAsync<EventRow[]>("/api/events");
        rows.ShouldHaveSingleItem().Kind.ShouldBe(nameof(SettingsChanged));
    }

    [Test]
    public async Task Events_PagedByBefore()
    {
        var viewer = await ClientAsync(UserRole.Viewer);
        var events = Server.Services.GetRequiredService<IDomainEvents>();
        for (var i = 0; i < 3; i++) events.Publish(new SettingsChanged(Actor.System, $"изменение {i}"));

        EventRow[] all = [];
        await EventuallyAsync(async () => (all = await viewer.GetJsonAsync<EventRow[]>("/api/events")).Length == 3);
        all.Select(e => e.Id).ShouldBeInOrder(SortDirection.Descending);

        var older = await viewer.GetJsonAsync<EventRow[]>($"/api/events?before={all[0].Id}&take=1");
        older.ShouldHaveSingleItem().Id.ShouldBe(all[1].Id);
    }

    [Test]
    public async Task UiHub_ReceivesChanged()
    {
        await ClientAsync(UserRole.Viewer);
        var token = await TokenAsync(ServerFactory.AdminLogin, ServerFactory.AdminPassword);
        await using var hub = UiHub(token);
        var received = new TaskCompletionSource<(string Entity, string Id)>();
        hub.On<string, string>("changed", (entity, id) => received.TrySetResult((entity, id)));
        await hub.StartAsync();

        // StartAsync завершается по ответу на рукопожатие, а в Clients.All сервер добавляет подключение чуть позже:
        // событие, опубликованное в этот промежуток, клиенту не уходит. Публикуем, пока клиент не получит.
        var runId = Guid.NewGuid();
        var events = Server.Services.GetRequiredService<IDomainEvents>();
        for (var attempt = 0; attempt < 50 && !received.Task.IsCompleted; attempt++)
        {
            events.Publish(new RunChanged(Actor.System, runId));
            await Task.WhenAny(received.Task, Task.Delay(200));
        }

        var (entity, changedId) = await received.Task.WaitAsync(TimeSpan.FromSeconds(5));
        entity.ShouldBe("runs");
        changedId.ShouldBe(runId.ToString());
    }

    [Test]
    public async Task UiHub_WithoutToken_Rejected()
    {
        await using var hub = UiHub(null);

        await Should.ThrowAsync<HttpRequestException>(() => hub.StartAsync());
    }

    private HubConnection UiHub(string? token) => new HubConnectionBuilder()
        .WithUrl(new Uri(Server.Server.BaseAddress, "/hubs/ui"), options =>
        {
            options.Transports = HttpTransportType.LongPolling;
            options.HttpMessageHandlerFactory = _ => Server.Server.CreateHandler();
            if (token is not null) options.AccessTokenProvider = () => Task.FromResult<string?>(token);
        })
        .Build();
}
