using Microsoft.EntityFrameworkCore;
using Shouldly;
using ToadFlow.Contracts;
using ToadFlow.Server.Families.Data;
using ToadFlow.Server.Families.Panel;
using ToadFlow.Server.Families.Requests;
using ToadFlow.Server.Notifications;
using ToadFlow.Server.Tests.Infrastructure;
using static ToadFlow.Server.Tests.Infrastructure.FamiliesFixtures;

namespace ToadFlow.Server.Tests;

/// <summary>Уведомления по библиотеке: семейство обновилось — тем, кто его загружал; пришло на проверку — координаторам направления.</summary>
public class FamiliesNotificationTests : ServerTest
{
    public FamiliesNotificationTests() => Services = WithoutScanLoop;

    private FamilyLibrary _library = null!;

    [After(Test)]
    public async Task StopAgentAsync() => await _library.DisposeAsync();

    private sealed record Me(Guid Id);

    private static async Task<Guid> IdAsync(HttpClient client) => (await client.GetJsonAsync<Me>("/api/profile")).Id;

    private static async Task<List<NotificationView>> ListAsync(HttpClient client) => await client.GetJsonAsync<List<NotificationView>>("/api/notifications");

    [Test]
    public async Task FamilyApproved_NotifiesLoaders()
    {
        _library = await FamilyLibrary.StartAsync(Server, await AdminAsync());
        await _library.ScanAsync(File(@"ОВ\Клапан.rfa", "a"));
        var card = await DbAsync(db => db.Set<FamilyCard>().Where(c => c.Name == "Клапан").Select(c => c.Id).SingleAsync());
        var loader = await ClientAsync(UserRole.Designer);
        var bystander = await ClientAsync(UserRole.Designer);
        (await loader.PostJsonAsync($"/api/families/{card}/loads", new LoadReport(Hash("a"), "ЖК Север"))).EnsureSuccessStatusCode();
        (await loader.PostJsonAsync($"/api/families/{card}/loads", new LoadReport(Hash("a"), "Дом на Набережной"))).EnsureSuccessStatusCode();

        await _library.ScanAsync(File(@"ОВ\Клапан.rfa", "b"));

        var notification = (await ListAsync(loader)).Single();
        (notification.Kind, notification.Link).ShouldBe((NotificationKinds.FamilyUpdated, $"/families/catalog?card={card}"));
        notification.Text.ShouldContain("ЖК Север");
        notification.Text.ShouldContain("Дом на Набережной");
        (await ListAsync(bystander)).ShouldBeEmpty();
    }

    [Test]
    public async Task VersionInReview_NotifiesDirection()
    {
        var ov = await ClientAsync(UserRole.Operator);
        var ar = await ClientAsync(UserRole.Operator);
        (await (await AdminAsync()).PutJsonAsync("/api/families/directions", new[]
        {
            new DirectionInput("ОВ/ВК", ["ОВ"], [], [await IdAsync(ov)], null), new DirectionInput("АР/КР", ["АР"], [], [await IdAsync(ar)], null)
        })).EnsureSuccessStatusCode();
        _library = await FamilyLibrary.StartAsync(Server, await AdminAsync());

        await _library.ScanAsync(File(@"_На проверке\ОВ\Задвижка.rfa", "z"));

        (await ListAsync(ov)).Select(n => (n.Kind, n.Link)).ShouldBe([(NotificationKinds.FamilyReview, "/families/review")]);
        (await ListAsync(ar)).ShouldBeEmpty();
    }
}
