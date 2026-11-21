using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using ToadFlow.Contracts;
using ToadFlow.Server.Data.Entities;
using ToadFlow.Server.Events;
using ToadFlow.Server.Notifications;
using ToadFlow.Server.Tests.Infrastructure;

namespace ToadFlow.Server.Tests;

/// <summary>Уведомления ядра: ошибки в расписании, за которым следишь, и заявки на регистрацию.</summary>
public class NotificationSourcesTests : ServerTest
{
    private sealed record Me(Guid Id);

    private static async Task<Guid> IdAsync(HttpClient client) => (await client.GetJsonAsync<Me>("/api/profile")).Id;

    private static async Task<List<NotificationView>> ListAsync(HttpClient client) => await client.GetJsonAsync<List<NotificationView>>("/api/notifications");

    private async Task<ScheduleView> ScheduleAsync(HttpClient client)
    {
        var admin = await AdminAsync();
        return await ScheduleApi.CreateAsync(client, ScheduleApi.Body(await ScheduleApi.SourceAsync(admin), await ScheduleApi.DefaultPresetAsync(admin)));
    }

    [Test]
    public async Task Follow_Unfollow_CreatorFollowsByDefault()
    {
        var creator = await ClientAsync(UserRole.Operator);
        var viewer = await ClientAsync(UserRole.Viewer);
        var schedule = await ScheduleAsync(creator);

        (await creator.GetJsonAsync<List<Guid>>("/api/schedules/following")).ShouldBe([schedule.Id]);
        (await viewer.GetJsonAsync<List<Guid>>("/api/schedules/following")).ShouldBeEmpty();
        (await viewer.PostAsync($"/api/schedules/{schedule.Id}/follow", null)).EnsureSuccessStatusCode();
        (await viewer.GetJsonAsync<List<Guid>>("/api/schedules/following")).ShouldBe([schedule.Id]);
        (await creator.DeleteAsync($"/api/schedules/{schedule.Id}/follow")).EnsureSuccessStatusCode();
        (await creator.GetJsonAsync<List<Guid>>("/api/schedules/following")).ShouldBeEmpty();
        (await viewer.PostAsync($"/api/schedules/{Guid.NewGuid()}/follow", null)).StatusCode.ShouldBe(System.Net.HttpStatusCode.NotFound);
    }

    [Test]
    public async Task RunFailed_NotifiesFollowersAndTrigger()
    {
        var creator = await ClientAsync(UserRole.Operator);
        var follower = await ClientAsync(UserRole.Viewer);
        var stranger = await ClientAsync(UserRole.Viewer);
        var schedule = await ScheduleAsync(creator);
        (await follower.PostAsync($"/api/schedules/{schedule.Id}/follow", null)).EnsureSuccessStatusCode();
        var runId = Guid.NewGuid();
        await DbAsync(async db =>
        {
            db.Runs.Add(new Run { Id = runId, ScheduleId = schedule.Id, ModuleName = "export", Name = "ЖК Север", State = RunState.Completed, CreatedAt = DateTimeOffset.UtcNow });
            await db.SaveChangesAsync();
        });

        Server.Services.GetRequiredService<IDomainEvents>().Publish(new RunFinished(Actor.System, runId, "ЖК Север", 3, 0, 2, 0));

        await EventuallyAsync(async () => (await ListAsync(follower)).Count == 1);
        var notification = (await ListAsync(creator)).Single();
        (notification.Kind, notification.Link).ShouldBe((NotificationKinds.RunFailed, $"/export/history/runs/{runId}"));
        notification.Text.ShouldContain("2");
        (await ListAsync(stranger)).ShouldBeEmpty();
    }

    [Test]
    public async Task RunFinished_OnlyFailuresNotify()
    {
        var creator = await ClientAsync(UserRole.Operator);
        var schedule = await ScheduleAsync(creator);
        var runId = Guid.NewGuid();
        await DbAsync(async db =>
        {
            db.Runs.Add(new Run { Id = runId, ScheduleId = schedule.Id, ModuleName = "export", Name = "ЖК Север", State = RunState.Completed, CreatedAt = DateTimeOffset.UtcNow });
            await db.SaveChangesAsync();
        });

        Server.Services.GetRequiredService<IDomainEvents>().Publish(new RunFinished(Actor.System, runId, "ЖК Север", 3, 1, 0, 0));
        Server.Services.GetRequiredService<IDomainEvents>().Publish(new RunFinished(Actor.System, runId, "ЖК Север", 3, 1, 1, 0));

        await EventuallyAsync(async () => (await ListAsync(creator)).Count == 1);
        (await ListAsync(creator)).Single().Text.ShouldContain("1");
    }

    [Test]
    public async Task Registration_NotifiesApprovers()
    {
        var coordinator = await ClientAsync(UserRole.Operator);
        var viewer = await ClientAsync(UserRole.Viewer);

        (await Server.CreateClient().PostJsonAsync("/api/auth/register", new { login = "kovaleva", displayName = "Ольга Ковалёва", password = "secret-123" }))
            .EnsureSuccessStatusCode();

        var notification = (await ListAsync(coordinator)).Single();
        (notification.Kind, notification.Link).ShouldBe((NotificationKinds.UserPending, "/admin/users"));
        notification.Text.ShouldContain("Ольга Ковалёва");
        (await ListAsync(viewer)).ShouldBeEmpty();
    }
}
