using Shouldly;
using ToadFlow.Contracts;
using ToadFlow.Server.Families.Data;
using ToadFlow.Server.Families.Requests;
using ToadFlow.Server.Search;
using ToadFlow.Server.Tests.Infrastructure;
using static ToadFlow.Server.Tests.Infrastructure.FamiliesFixtures;

namespace ToadFlow.Server.Tests;

/// <summary>Быстрый поиск по всему сервису (Ctrl+K): семейства, заявки, расписания, пользователи — каждому своё.</summary>
public class GlobalSearchTests : ServerTest
{
    public GlobalSearchTests() => Services = WithoutScanLoop;

    private sealed record Created(Guid Id, int Number);

    private static Task<List<SearchHit>> SearchAsync(HttpClient client, string q) => client.GetJsonAsync<List<SearchHit>>($"/api/search?q={Uri.EscapeDataString(q)}");

    [Test]
    public async Task Search_FindsRequestByNumberAndFamilyByName()
    {
        var designer = await ClientAsync(UserRole.Designer);
        var card = await DbAsync(db => SeedCardAsync(db, "Клапан обратный", @"ОВ\Клапан обратный.rfa", CardState.Approved, null, DateTimeOffset.UtcNow));
        var created = await (await designer.PostJsonAsync("/api/families/requests", new NewRequest("Задвижка шиберная", null, null, null, null)))
            .ReadJsonAsync<Created>();

        (await SearchAsync(designer, created.Number.ToString())).ShouldContain(h => h.Kind == "request" && h.Link == $"/families/requests/{created.Id}");
        (await SearchAsync(designer, "задвижка")).ShouldContain(h => h.Kind == "request" && h.Title.Contains("Задвижка шиберная"));
        var family = (await SearchAsync(designer, "клапан")).Single(h => h.Kind == "family");
        family.Link.ShouldBe($"/families/catalog?card={card}");
    }

    [Test]
    public async Task Search_Designer_NoSchedulesNoUsers()
    {
        var admin = await AdminAsync();
        var schedule = await ScheduleApi.CreateAsync(admin, ScheduleApi.Body(await ScheduleApi.SourceAsync(admin), await ScheduleApi.DefaultPresetAsync(admin), "Выгрузка Север"));

        var forAdmin = await SearchAsync(admin, "север");
        forAdmin.ShouldContain(h => h.Kind == "schedule" && h.Link == $"/export/schedules/{schedule.Id}");
        (await SearchAsync(admin, "admin")).ShouldContain(h => h.Kind == "user");

        var designer = await ClientAsync(UserRole.Designer);
        (await SearchAsync(designer, "север")).ShouldNotContain(h => h.Kind == "schedule");
        (await SearchAsync(designer, "admin")).ShouldNotContain(h => h.Kind == "user");
        (await SearchAsync(await ClientAsync(UserRole.Operator), "admin")).ShouldNotContain(h => h.Kind == "user");
    }

    [Test]
    public async Task Search_ShortQuery_Empty_AndCappedPerKind()
    {
        var designer = await ClientAsync(UserRole.Designer);
        for (var i = 0; i < 8; i++) await DbAsync(db => SeedCardAsync(db, $"Клапан {i}", $@"ОВ\Клапан {i}.rfa", CardState.Approved, null, DateTimeOffset.UtcNow));

        (await SearchAsync(designer, "к")).ShouldBeEmpty();
        (await SearchAsync(designer, "клапан")).Count(h => h.Kind == "family").ShouldBe(5);
    }
}

/// <summary>Выключенный модуль в поиске молчит.</summary>
public class GlobalSearchDisabledModuleTests : ServerTest
{
    public GlobalSearchDisabledModuleTests() => Modules = "export";

    [Test]
    public async Task Search_DisabledModuleSilent()
    {
        var hits = await (await ClientAsync(UserRole.Designer)).GetJsonAsync<List<SearchHit>>("/api/search?q=клапан");

        hits.ShouldBeEmpty();
    }
}
