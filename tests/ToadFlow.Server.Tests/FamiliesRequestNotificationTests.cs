using Shouldly;
using ToadFlow.Contracts;
using ToadFlow.Server.Families.Data;
using ToadFlow.Server.Families.Requests;
using ToadFlow.Server.Notifications;
using ToadFlow.Server.Tests.Infrastructure;
using static ToadFlow.Server.Tests.Infrastructure.FamiliesFixtures;

namespace ToadFlow.Server.Tests;

/// <summary>Уведомления по заявкам: кому приходит каждое событие.</summary>
public class FamiliesRequestNotificationTests : ServerTest
{
    public FamiliesRequestNotificationTests() => Services = WithoutScanLoop;

    private sealed record Created(Guid Id, int Number);

    private sealed record Me(Guid Id);

    private static async Task<Guid> IdAsync(HttpClient client) => (await client.GetJsonAsync<Me>("/api/profile")).Id;

    private static async Task<Created> CreateAsync(HttpClient client, string section = "ОВ")
    {
        var response = await client.PostJsonAsync("/api/families/requests", new NewRequest("Клапан", null, section, null, null));
        response.EnsureSuccessStatusCode();
        return await response.ReadJsonAsync<Created>();
    }

    private static async Task<List<string>> KindsAsync(HttpClient client) =>
        (await client.GetJsonAsync<List<NotificationView>>("/api/notifications")).Select(n => n.Kind).ToList();

    private static async Task PostAsync(HttpClient client, string url, object? body = null) =>
        (body is null ? await client.PostAsync(url, null) : await client.PostJsonAsync(url, body)).EnsureSuccessStatusCode();

    [Test]
    public async Task New_GoesToDirectionCoordinatorsOnly()
    {
        var ov = await ClientAsync(UserRole.Operator);
        var ar = await ClientAsync(UserRole.Operator);
        (await (await AdminAsync()).PutJsonAsync("/api/families/directions", new[]
        {
            new DirectionInput("ОВ/ВК", ["ОВ"], [], [await IdAsync(ov)], null), new DirectionInput("АР/КР", ["АР"], [], [await IdAsync(ar)], null)
        })).EnsureSuccessStatusCode();

        await CreateAsync(await ClientAsync(UserRole.Designer));

        (await KindsAsync(ov)).ShouldBe([NotificationKinds.RequestNew]);
        (await KindsAsync(ar)).ShouldBeEmpty();
    }

    [Test]
    public async Task New_NoDirection_AllStaff()
    {
        var first = await ClientAsync(UserRole.Operator);
        var second = await ClientAsync(UserRole.Operator);
        var designer = await ClientAsync(UserRole.Designer);

        await CreateAsync(designer);

        (await KindsAsync(first)).ShouldBe([NotificationKinds.RequestNew]);
        (await KindsAsync(second)).ShouldBe([NotificationKinds.RequestNew]);
        (await KindsAsync(designer)).ShouldBeEmpty();
    }

    [Test]
    public async Task Assign_Ask_Message_Join_Notify()
    {
        var author = await ClientAsync(UserRole.Designer);
        var joined = await ClientAsync(UserRole.Designer);
        var coordinator = await ClientAsync(UserRole.Operator);
        var assignee = await ClientAsync(UserRole.Operator);
        var created = await CreateAsync(author);
        var url = $"/api/families/requests/{created.Id}";

        await PostAsync(coordinator, $"{url}/assign", new RequestAssign(await IdAsync(assignee)));
        (await KindsAsync(assignee)).ShouldContain(NotificationKinds.RequestAssigned);

        await PostAsync(joined, $"{url}/join");
        (await KindsAsync(assignee)).ShouldContain(NotificationKinds.RequestJoined);

        await PostAsync(assignee, $"{url}/ask", new RequestMessageRequest("Какой DN?"));
        (await KindsAsync(author)).ShouldContain(NotificationKinds.RequestAsk);
        (await KindsAsync(joined)).ShouldContain(NotificationKinds.RequestAsk);

        await PostAsync(author, $"{url}/messages", new RequestMessageRequest("DN50"));
        (await KindsAsync(assignee)).ShouldContain(NotificationKinds.RequestMessage);
        (await KindsAsync(joined)).ShouldContain(NotificationKinds.RequestMessage);
        (await KindsAsync(author)).ShouldNotContain(NotificationKinds.RequestMessage);
    }

    [Test]
    public async Task Review_Decline_Accept_Notify()
    {
        var author = await ClientAsync(UserRole.Designer);
        var coordinator = await ClientAsync(UserRole.Operator);
        var card = await DbAsync(db => SeedCardAsync(db, "Клапан", @"ОВ\Клапан.rfa", CardState.Approved, null, DateTimeOffset.UtcNow));
        var created = await CreateAsync(author);
        var url = $"/api/families/requests/{created.Id}";
        await PostAsync(coordinator, $"{url}/take");

        await PostAsync(coordinator, $"{url}/done", new RequestCard(card));
        (await KindsAsync(author)).ShouldContain(NotificationKinds.RequestReview);

        await PostAsync(author, $"{url}/decline", new RequestMessageRequest("не тот"));
        (await KindsAsync(coordinator)).ShouldContain(NotificationKinds.RequestDeclined);

        await PostAsync(coordinator, $"{url}/done", new RequestCard(card));
        await PostAsync(author, $"{url}/accept");
        (await KindsAsync(coordinator)).ShouldContain(NotificationKinds.RequestClosed);
    }

    [Test]
    public async Task Reject_NotifiesMembers()
    {
        var author = await ClientAsync(UserRole.Designer);
        var coordinator = await ClientAsync(UserRole.Operator);
        var created = await CreateAsync(author);

        await PostAsync(coordinator, $"/api/families/requests/{created.Id}/reject", new RequestReject("не наш раздел"));

        (await KindsAsync(author)).ShouldContain(NotificationKinds.RequestClosed);
    }

    [Test]
    public async Task DirectionChange_NotifiesNewCoordinators()
    {
        var ar = await ClientAsync(UserRole.Operator);
        var coordinator = await ClientAsync(UserRole.Operator);
        var saved = await (await (await AdminAsync()).PutJsonAsync("/api/families/directions", new[]
        {
            new DirectionInput("ОВ/ВК", ["ОВ"], [], [await IdAsync(coordinator)], null), new DirectionInput("АР/КР", ["АР"], [], [await IdAsync(ar)], null)
        })).ReadJsonAsync<List<DirectionView>>();
        var created = await CreateAsync(await ClientAsync(UserRole.Designer));
        (await KindsAsync(ar)).ShouldBeEmpty();

        await PostAsync(coordinator, $"/api/families/requests/{created.Id}/direction", new RequestDirection(saved[1].Id));

        (await KindsAsync(ar)).ShouldBe([NotificationKinds.RequestNew]);
    }
}
