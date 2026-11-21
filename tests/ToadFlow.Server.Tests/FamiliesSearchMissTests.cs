using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using ToadFlow.Contracts;
using ToadFlow.Server.Families.Data;
using ToadFlow.Server.Families.Requests;
using ToadFlow.Server.Families.Search;
using ToadFlow.Server.Runs;
using ToadFlow.Server.Tests.Infrastructure;
using static ToadFlow.Server.Tests.Infrastructure.FamiliesFixtures;

namespace ToadFlow.Server.Tests;

/// <summary>«Искали и не нашли»: поиски без результата — координатору план библиотеки.</summary>
public class FamiliesSearchMissTests : ServerTest
{
    public FamiliesSearchMissTests() => Services = WithoutScanLoop;

    private static Task<Page<FamilyHit>> SearchAsync(HttpClient client, string q, string extra = "") =>
        client.GetJsonAsync<Page<FamilyHit>>($"/api/families/search?q={Uri.EscapeDataString(q)}{extra}");

    private static Task<List<MissRow>> MissesAsync(HttpClient client) => client.GetJsonAsync<List<MissRow>>("/api/families/misses");

    [Test]
    public async Task Miss_LoggedOnZeroResults_HitNotLogged()
    {
        await DbAsync(db => SeedCardAsync(db, "Клапан обратный", @"ОВ\Клапан обратный.rfa", CardState.Approved, null, DateTimeOffset.UtcNow));
        var designer = await ClientAsync(UserRole.Designer);
        var coordinator = await ClientAsync(UserRole.Operator);

        await SearchAsync(designer, "клапан");
        await SearchAsync(designer, "задвижка шиберная");
        await SearchAsync(designer, "ог");
        await SearchAsync(designer, "решётка", "&section=ОВ");

        (await MissesAsync(coordinator)).Select(m => (m.Query, m.Count, m.Users)).ShouldBe([("задвижка шиберная", 1, 1)]);
        (await designer.GetAsync("/api/families/misses")).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Test]
    public async Task Miss_TypingGluedIntoLast()
    {
        var designer = await ClientAsync(UserRole.Designer);
        var coordinator = await ClientAsync(UserRole.Operator);

        await SearchAsync(designer, "задв");
        await SearchAsync(designer, "задвиж");
        await SearchAsync(designer, "задвижка шибер");
        await SearchAsync(designer, "решётка", "&source=panel");

        var misses = await MissesAsync(coordinator);
        misses.Select(m => m.Query).ShouldBe(["задвижка шибер", "решётка"], ignoreOrder: true);
        (await DbAsync(db => db.Set<FamilySearchMiss>().Where(m => m.Query == "решётка").Select(m => m.Source).SingleAsync())).ShouldBe("panel");
    }

    [Test]
    public async Task Misses_Aggregated_WithOpenRequest()
    {
        var first = await ClientAsync(UserRole.Designer);
        var second = await ClientAsync(UserRole.Designer);
        var coordinator = await ClientAsync(UserRole.Operator);
        await SearchAsync(first, "Задвижка шиберная");
        await SearchAsync(second, "задвижка шиберная");
        await SearchAsync(first, "отвод");
        var created = await (await first.PostJsonAsync("/api/families/requests", new NewRequest("Задвижка шиберная Ду100", null, null, null, null)))
            .ReadJsonAsync<RequestCreated>();

        var misses = await MissesAsync(coordinator);

        misses.Select(m => (m.Query, m.Count, m.Users)).ShouldBe([("задвижка шиберная", 2, 2), ("отвод", 1, 1)]);
        (misses[0].RequestId, misses[0].RequestNumber).ShouldBe(((Guid?)created.Id, (int?)created.Number));
        misses[1].RequestId.ShouldBeNull();
    }

    [Test]
    public async Task Maintenance_RemovesMissesOlderThan180Days()
    {
        var designer = await ClientAsync(UserRole.Designer);
        await SearchAsync(designer, "задвижка");

        await using (var scope = Server.Services.CreateAsyncScope())
            await RequestMaintenance.RunOnceAsync(scope.ServiceProvider, DateTimeOffset.UtcNow.AddDays(181), CancellationToken.None);

        (await DbAsync(db => db.Set<FamilySearchMiss>().CountAsync())).ShouldBe(0);
    }

    private sealed record RequestCreated(Guid Id, int Number);
}
