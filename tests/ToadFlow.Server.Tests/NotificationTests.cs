using System.Net;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using ToadFlow.Contracts;
using ToadFlow.Server.Notifications;
using ToadFlow.Server.Tests.Infrastructure;

namespace ToadFlow.Server.Tests;

/// <summary>Уведомления: кому доходят, склейка, прочтение, отключение видов, уборка старых.</summary>
public class NotificationTests : ServerTest
{
    private sealed record Me(Guid Id);

    private static async Task<Guid> IdAsync(HttpClient client) => (await client.GetJsonAsync<Me>("/api/profile")).Id;

    private async Task NotifyAsync(IEnumerable<Guid> users, string kind, string text, string? link = "/x", string? groupKey = null, Guid? actor = null)
    {
        await using var scope = Server.Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<INotifications>()
            .NotifyAsync(users, kind, "Заявка № 1", text, link, groupKey, actor, CancellationToken.None);
    }

    private static Task<List<NotificationView>> ListAsync(HttpClient client) => client.GetJsonAsync<List<NotificationView>>("/api/notifications");

    private static async Task<int> CountAsync(HttpClient client) => (await client.GetJsonAsync<NotificationCount>("/api/notifications/count")).Unread;

    [Test]
    public async Task Notify_SkipsActorMutedAndDisabled()
    {
        var actor = await ClientAsync(UserRole.Operator);
        var reader = await ClientAsync(UserRole.Designer);
        var muted = await ClientAsync(UserRole.Designer);
        var disabled = await ClientAsync(UserRole.Designer);
        var (actorId, readerId, mutedId, disabledId) = (await IdAsync(actor), await IdAsync(reader), await IdAsync(muted), await IdAsync(disabled));
        (await muted.PutJsonAsync("/api/notifications/settings", new NotificationSettingsInput([NotificationKinds.RequestMessage]))).EnsureSuccessStatusCode();
        await DbAsync(db => db.Users.Where(u => u.Id == disabledId).ExecuteUpdateAsync(u => u.SetProperty(x => x.IsDisabled, true)));

        await NotifyAsync([actorId, readerId, mutedId, disabledId], NotificationKinds.RequestMessage, "новое сообщение", actor: actorId);

        (await ListAsync(reader)).Select(n => (n.Kind, n.Title, n.Text, n.Link, n.Read)).ShouldBe([(NotificationKinds.RequestMessage, "Заявка № 1", "новое сообщение", "/x", false)]);
        (await ListAsync(actor)).ShouldBeEmpty();
        (await ListAsync(muted)).ShouldBeEmpty();
        (await DbAsync(db => db.Set<Notification>().CountAsync(n => n.UserId == disabledId))).ShouldBe(0);
    }

    [Test]
    public async Task Notify_GluesSameKeyUntilRead()
    {
        var reader = await ClientAsync(UserRole.Designer);
        var id = await IdAsync(reader);

        await NotifyAsync([id], NotificationKinds.RequestMessage, "первое", groupKey: "request:1:message");
        await NotifyAsync([id], NotificationKinds.RequestMessage, "второе", groupKey: "request:1:message");
        await NotifyAsync([id], NotificationKinds.RequestMessage, "другая заявка", groupKey: "request:2:message");

        var list = await ListAsync(reader);
        list.Select(n => (n.Text, n.Count)).ShouldBe([("другая заявка", 1), ("второе", 2)]);
        (await reader.PostAsync($"/api/notifications/{list[1].Id}/read", null)).EnsureSuccessStatusCode();
        await NotifyAsync([id], NotificationKinds.RequestMessage, "третье", groupKey: "request:1:message");
        (await ListAsync(reader)).Count.ShouldBe(3);
    }

    [Test]
    public async Task Count_ReadAll_ReadLink()
    {
        var reader = await ClientAsync(UserRole.Designer);
        var other = await ClientAsync(UserRole.Designer);
        var id = await IdAsync(reader);
        await NotifyAsync([id], NotificationKinds.RequestReview, "а", "/families/requests/a");
        await NotifyAsync([id], NotificationKinds.RequestAsk, "а2", "/families/requests/a");
        await NotifyAsync([id], NotificationKinds.RequestReview, "б", "/families/requests/b");

        (await CountAsync(reader)).ShouldBe(3);
        (await reader.PostJsonAsync("/api/notifications/read-link", new NotificationLink("/families/requests/a"))).EnsureSuccessStatusCode();
        (await CountAsync(reader)).ShouldBe(1);
        var unread = (await ListAsync(reader)).Single(n => !n.Read);
        (await other.PostAsync($"/api/notifications/{unread.Id}/read", null)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await reader.PostAsync("/api/notifications/read-all", null)).EnsureSuccessStatusCode();
        (await CountAsync(reader)).ShouldBe(0);
    }

    [Test]
    public async Task Settings_RoundTrip()
    {
        var reader = await ClientAsync(UserRole.Viewer);

        var before = await reader.GetJsonAsync<NotificationSettingsView>("/api/notifications/settings");
        before.Kinds.ShouldContain(k => k.Kind == NotificationKinds.RunFailed && k.Title.Length > 0 && !k.Muted);
        (await reader.PutJsonAsync("/api/notifications/settings", new NotificationSettingsInput([NotificationKinds.RunFailed, "нет-такого"])))
            .StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await reader.PutJsonAsync("/api/notifications/settings", new NotificationSettingsInput([NotificationKinds.RunFailed]))).EnsureSuccessStatusCode();

        var after = await reader.GetJsonAsync<NotificationSettingsView>("/api/notifications/settings");
        after.Kinds.Where(k => k.Muted).Select(k => k.Kind).ShouldBe([NotificationKinds.RunFailed]);
    }

    [Test]
    public async Task List_PagesBeforeId()
    {
        var reader = await ClientAsync(UserRole.Designer);
        var id = await IdAsync(reader);
        for (var i = 0; i < 5; i++) await NotifyAsync([id], NotificationKinds.RequestNew, $"№{i}");

        var first = await reader.GetJsonAsync<List<NotificationView>>("/api/notifications?take=2");
        var next = await reader.GetJsonAsync<List<NotificationView>>($"/api/notifications?take=2&before={first[^1].Id}");

        first.Select(n => n.Text).ShouldBe(["№4", "№3"]);
        next.Select(n => n.Text).ShouldBe(["№2", "№1"]);
    }

    /// <summary>Браузер по WebSocket передаёт токен в строке адреса: личный хаб должен его принять — и у проектировщика.</summary>
    [Test]
    public async Task MeHub_AcceptsTokenInQuery_ForDesigner()
    {
        var login = $"des-{Guid.NewGuid():N}"[..16];
        await DbAsync(async db =>
        {
            var user = ToadFlow.Server.Data.Entities.User.Create(login, "Проектировщик", UserRole.Designer, "", DateTimeOffset.UtcNow);
            user.PasswordHash = new Microsoft.AspNetCore.Identity.PasswordHasher<ToadFlow.Server.Data.Entities.User>().HashPassword(user, UserPassword);
            db.Users.Add(user);
            await db.SaveChangesAsync();
        });
        var token = await TokenAsync(login, UserPassword);

        var response = await Server.CreateClient().PostAsync($"/hubs/me/negotiate?negotiateVersion=1&access_token={token}", null);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Test]
    public async Task MeHub_DeliversToRecipientOnly()
    {
        var reader = await ClientAsync(UserRole.Designer);
        var id = await IdAsync(reader);
        var token = reader.DefaultRequestHeaders.Authorization!.Parameter!;
        await using var hub = new HubConnectionBuilder()
            .WithUrl(new Uri(Server.Server.BaseAddress, "/hubs/me"), options =>
            {
                options.Transports = Microsoft.AspNetCore.Http.Connections.HttpTransportType.LongPolling;
                options.HttpMessageHandlerFactory = _ => Server.Server.CreateHandler();
                options.AccessTokenProvider = () => Task.FromResult<string?>(token);
            })
            .Build();
        var received = new TaskCompletionSource();
        hub.On("notification", () => received.TrySetResult());
        await hub.StartAsync();

        await EventuallyAsync(async () =>
        {
            await NotifyAsync([id], NotificationKinds.RequestNew, "новая");
            return await Task.WhenAny(received.Task, Task.Delay(500)) == received.Task;
        });
    }

    [Test]
    public async Task Cleanup_RemovesReadOlderThan90Days()
    {
        var reader = await ClientAsync(UserRole.Designer);
        var id = await IdAsync(reader);
        var now = DateTimeOffset.UtcNow;
        await DbAsync(async db =>
        {
            db.Set<Notification>().AddRange(
                new Notification { UserId = id, Kind = NotificationKinds.RequestNew, Title = "т", Text = "старое прочитанное", At = now.AddDays(-100), ReadAt = now.AddDays(-95) },
                new Notification { UserId = id, Kind = NotificationKinds.RequestNew, Title = "т", Text = "старое непрочитанное", At = now.AddDays(-100) },
                new Notification { UserId = id, Kind = NotificationKinds.RequestNew, Title = "т", Text = "свежее прочитанное", At = now.AddDays(-10), ReadAt = now.AddDays(-9) });
            await db.SaveChangesAsync();
        });

        await DbAsync(db => NotificationCleanup.RunOnceAsync(db, now, CancellationToken.None));

        (await ListAsync(reader)).Select(n => n.Text).ShouldBe(["свежее прочитанное", "старое непрочитанное"], ignoreOrder: true);
    }
}
