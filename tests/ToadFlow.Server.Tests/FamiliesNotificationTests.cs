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
    public async Task SilentCard_NoUpdated()
    {
        _library = await FamilyLibrary.StartAsync(Server, await AdminAsync());
        await _library.ScanAsync(File(@"ОВ\Клапан.rfa", "a"));
        var card = await DbAsync(db => db.Set<FamilyCard>().Select(c => c.Id).SingleAsync());
        await DbAsync(db => db.Set<FamilyCard>().ExecuteUpdateAsync(c => c.SetProperty(x => x.Silent, true)));
        var loader = await ClientAsync(UserRole.Designer);
        (await loader.PostJsonAsync($"/api/families/{card}/loads", new LoadReport(Hash("a"), "ЖК Север"))).EnsureSuccessStatusCode();

        await _library.ScanAsync(File(@"ОВ\Клапан.rfa", "b"));

        (await ListAsync(loader)).ShouldBeEmpty();
    }

    [Test]
    public async Task SilentPublication_NoUpdated_ReviewStillSent()
    {
        var ov = await ClientAsync(UserRole.Operator);
        (await (await AdminAsync()).PutJsonAsync("/api/families/directions", new[] { new DirectionInput("ОВ/ВК", ["ОВ"], [], [await IdAsync(ov)], null) }))
            .EnsureSuccessStatusCode();
        _library = await FamilyLibrary.StartAsync(Server, await AdminAsync());
        await _library.ScanAsync(File(@"ОВ\Клапан.rfa", "a"));
        var card = await DbAsync(db => db.Set<FamilyCard>().Select(c => c.Id).SingleAsync());
        var loader = await ClientAsync(UserRole.Designer);
        (await loader.PostJsonAsync($"/api/families/{card}/loads", new LoadReport(Hash("a"), "ЖК Север"))).EnsureSuccessStatusCode();

        // Мелкая правка прямо на месте, без оповещения.
        (await _library.Admin.PostJsonAsync("/api/families/publications", new PublishRequest($@"{Root}\ОВ\Клапан.rfa", Hash("b"), card, "опечатка", null) { Silent = true }))
            .EnsureSuccessStatusCode();
        await _library.ReportLastAsync(File(@"ОВ\Клапан.rfa", "b"));
        (await ListAsync(loader)).ShouldBeEmpty();

        (await _library.Admin.PostJsonAsync("/api/families/publications",
            new PublishRequest($@"{Root}\_На проверке\ОВ\Клапан.rfa", Hash("c"), card, "новое", null) { Silent = true })).EnsureSuccessStatusCode();
        await _library.ReportLastAsync(File(@"ОВ\Клапан.rfa", "b"), File(@"_На проверке\ОВ\Клапан.rfa", "c"));
        (await ListAsync(ov)).Select(n => n.Kind).ShouldBe([NotificationKinds.FamilyReview]);
    }

    [Test]
    public async Task IterationUpdate_NotifiesOnlyIterationLoaders()
    {
        const string polk = @"_Итерации\Дверь\ЖК Полковая\Дверь.rfa";
        _library = await FamilyLibrary.StartAsync(Server, await AdminAsync());
        await _library.ScanAsync(File(@"АР\Дверь.rfa", "a"), File(polk, "a"));
        var card = await DbAsync(db => db.Set<FamilyCard>().Select(c => c.Id).SingleAsync());
        var iteration = await DbAsync(db => db.Set<FamilyIteration>().Select(i => i.Id).SingleAsync());
        var baseLoader = await ClientAsync(UserRole.Designer);
        var iterationLoader = await ClientAsync(UserRole.Designer);
        (await baseLoader.PostJsonAsync($"/api/families/{card}/loads", new LoadReport(Hash("a"), "ЖК Север"))).EnsureSuccessStatusCode();
        (await iterationLoader.PostJsonAsync($"/api/families/{card}/loads", new LoadReport(Hash("a"), "Полковая К1") { IterationId = iteration }))
            .EnsureSuccessStatusCode();

        await _library.ScanAsync(File(@"АР\Дверь.rfa", "a"), File(polk, "b"));

        var notification = (await ListAsync(iterationLoader)).ShouldHaveSingleItem();
        (notification.Kind, notification.Title).ShouldBe((NotificationKinds.FamilyUpdated, "Обновлено: Дверь · ЖК Полковая"));
        notification.Text.ShouldContain("Полковая К1");
        (await ListAsync(baseLoader)).ShouldBeEmpty();

        await _library.ScanAsync(File(@"АР\Дверь.rfa", "c"), File(polk, "b"));
        (await ListAsync(baseLoader)).ShouldHaveSingleItem().Title.ShouldBe("Обновлено: Дверь");
        (await ListAsync(iterationLoader)).Count.ShouldBe(1);
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
