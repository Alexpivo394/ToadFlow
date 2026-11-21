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

/// <summary>Фильтры журнала действий: кто (по вхождению, без учёта регистра), вид (точно), период — и листание вместе с ними.</summary>
public class EventFilterTests : ServerTest
{
    private sealed record EventRow(long Id, DateTimeOffset At, ActorKind ActorKind, string ActorName, string Kind, string Text);

    private static readonly DateTimeOffset Day = new(2026, 3, 10, 12, 0, 0, TimeSpan.Zero);

    /// <summary>События теста — с давней датой: очистка старых записей, идущая в фоне после старта сервера, могла бы стереть их до проверки.</summary>
    public EventFilterTests() => Services = services => services.Remove(services.Single(d =>
        d.ServiceType == typeof(Microsoft.Extensions.Hosting.IHostedService) && d.ImplementationType == typeof(ToadFlow.Server.Maintenance.RetentionCleaner)));

    private Task SeedAsync() => DbAsync(async db =>
    {
        void Add(string actor, string kind, DateTimeOffset at, string text) =>
            db.SystemEvents.Add(new SystemEvent { At = at, ActorKind = ActorKind.User, ActorName = actor, Kind = kind, Text = text });

        Add("Иван Петров", "ScheduleChanged", Day, "1");
        Add("Пётр Иванов", "ScheduleChanged", Day.AddDays(1), "2");
        Add("Иван Петров", "SettingsChanged", Day.AddDays(2), "3");
        Add("Мария", "UserChanged", Day.AddDays(3), "4");
        Add("иван сидоров", "UserChanged", Day.AddDays(4), "5");
        await db.SaveChangesAsync();
    });

    private async Task<string[]> TextsAsync(string query) =>
        (await (await AdminAsync()).GetJsonAsync<EventRow[]>($"/api/events?{query}")).Where(e => e.At.Year == 2026 && e.At.Month == 3)
        .Select(e => e.Text).ToArray();

    [Test]
    public async Task Events_FilterByActor()
    {
        await SeedAsync();

        (await TextsAsync("actor=ИВАН")).ShouldBe(["5", "3", "2", "1"]);
        (await TextsAsync("actor=петров")).ShouldBe(["3", "1"]);
    }

    [Test]
    public async Task Events_FilterByKind()
    {
        await SeedAsync();

        (await TextsAsync("kind=UserChanged")).ShouldBe(["5", "4"]);
        (await TextsAsync("kind=User")).ShouldBeEmpty();
    }

    [Test]
    public async Task Events_FilterByPeriod()
    {
        await SeedAsync();

        (await TextsAsync($"from={Uri.EscapeDataString(Day.AddDays(1).ToString("O"))}&to={Uri.EscapeDataString(Day.AddDays(3).ToString("O"))}"))
            .ShouldBe(["4", "3", "2"]);
    }

    [Test]
    public async Task Events_FiltersCombineWithPaging()
    {
        await SeedAsync();
        var admin = await AdminAsync();

        var first = await admin.GetJsonAsync<EventRow[]>("/api/events?actor=иван&take=2");
        var next = await admin.GetJsonAsync<EventRow[]>($"/api/events?actor=иван&take=2&before={first[^1].Id}");

        first.Select(e => e.Text).ShouldBe(["5", "3"]);
        next.Select(e => e.Text).ShouldBe(["2", "1"]);
    }

    [Test]
    public async Task EventKinds_Distinct()
    {
        await SeedAsync();

        var kinds = await (await AdminAsync()).GetJsonAsync<string[]>("/api/events/kinds");

        kinds.ShouldBe(kinds.Distinct().Order(StringComparer.Ordinal).ToArray());
        kinds.ShouldContain("ScheduleChanged");
        kinds.ShouldContain("UserChanged");
        kinds.ShouldContain("SettingsChanged");
    }
}
