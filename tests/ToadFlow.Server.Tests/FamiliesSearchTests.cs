using System.Net;
using Alexpivo394.Revit.Families.Files;
using Microsoft.EntityFrameworkCore;
using Shouldly;
using ToadFlow.Contracts;
using ToadFlow.Server.Families.Data;
using ToadFlow.Server.Families.Search;
using ToadFlow.Server.Runs;
using ToadFlow.Server.Tests.Infrastructure;

namespace ToadFlow.Server.Tests;

public class FamiliesSearchTests : ServerTest
{
    public FamiliesSearchTests() => Services = FamiliesFixtures.WithoutScanLoop;

    private static readonly DateTimeOffset Now = new(2026, 11, 20, 12, 0, 0, TimeSpan.Zero);

    private static FamilyContent Content(string category = "Арматура трубопроводов", params string[] types) =>
        new(category, "OneLevelBased", 2022, types.Select(t => new FamilyTypeInfo(t, new Dictionary<string, string> { ["Обозначение"] = t + "-обозн" })).ToList(),
            [new FamilyParameterInfo("Диаметр условный", false, null, false, "Размеры", "Длина")], false);

    private Task<Guid> SeedAsync(string name, string path, CardState state = CardState.Approved, FamilyContent? content = null, int minutesAgo = 0,
        string? description = null, int? year = 2022) =>
        DbAsync(db => FamiliesFixtures.SeedCardAsync(db, name, path, state, content, Now.AddMinutes(-minutesAgo), description, year));

    private static async Task<string[]> NamesAsync(HttpClient client, string query)
    {
        var page = await client.GetJsonAsync<Page<FamilyHit>>($"/api/families/search?{query}");
        return page.Items.Select(h => h.Name).ToArray();
    }

    [Test]
    public async Task Search_ByPartOfTypeName()
    {
        await SeedAsync("Клапан обратный", @"ОВ\Клапан обратный.rfa", content: Content(types: ["КЛАБ-40-20", "КЛАБ-50-20"]));
        await SeedAsync("Кран шаровой", @"ВК\Кран шаровой.rfa", content: Content(types: ["КШ-15"]));

        var names = await NamesAsync(await ClientAsync(UserRole.Designer), "q=клаб-40");

        names.ShouldBe(["Клапан обратный"]);
    }

    [Test]
    public async Task Search_ByParameterNameAndCategory()
    {
        await SeedAsync("Клапан обратный", @"ОВ\Клапан обратный.rfa", content: Content(types: ["DN50"]));
        await SeedAsync("Балка", @"КР\Балка.rfa", content: Content("Каркас несущий", "Б1"));
        var designer = await ClientAsync(UserRole.Designer);

        (await NamesAsync(designer, "q=условный")).ShouldBe(["Клапан обратный", "Балка"], ignoreOrder: true);
        (await NamesAsync(designer, "q=каркас")).ShouldBe(["Балка"]);
    }

    [Test]
    public async Task Search_AllWordsMustMatch()
    {
        await SeedAsync("Клапан обратный", @"ОВ\Клапан обратный.rfa");
        await SeedAsync("Клапан прямой", @"ОВ\Клапан прямой.rfa");

        var names = await NamesAsync(await ClientAsync(UserRole.Designer), "q=" + Uri.EscapeDataString("клапан  ОБРАТНЫЙ"));

        names.ShouldBe(["Клапан обратный"]);
    }

    [Test]
    public async Task Search_NameMatchesFirst()
    {
        await SeedAsync("Кран", @"ВК\Кран.rfa", description: "ставится вместо клапана", minutesAgo: 0);
        await SeedAsync("Клапан", @"ОВ\Клапан.rfa", minutesAgo: 60);

        var names = await NamesAsync(await ClientAsync(UserRole.Designer), "q=клапан");

        names.ShouldBe(["Клапан", "Кран"]);
    }

    [Test]
    [Arguments("%")]
    [Arguments("_")]
    [Arguments("'")]
    [Arguments("\"")]
    [Arguments("a")]
    [Arguments("\\")]
    public async Task Search_SpecialCharacters_Literal(string q)
    {
        await SeedAsync("Клапан 100%", @"ОВ\Клапан 100%.rfa");
        await SeedAsync("Клапан_2", @"ОВ\Клапан_2.rfa");
        await SeedAsync("Клапан 'А'", @"ОВ\Клапан 'А'.rfa");
        await SeedAsync("Клапан 3", @"ОВ\Клапан 3.rfa", description: "тип \"Б\"");
        await SeedAsync("Задвижка a", @"ОВ\Задвижка a.rfa");
        await SeedAsync("Отвод", @"ОВ\Отвод.rfa", description: @"папка С:\стандарт");
        var expected = q switch
        {
            "%" => "Клапан 100%",
            "_" => "Клапан_2",
            "'" => "Клапан 'А'",
            "\"" => "Клапан 3",
            "a" => "Задвижка a",
            _ => "Отвод"
        };

        var response = await (await ClientAsync(UserRole.Designer)).GetAsync($"/api/families/search?q={Uri.EscapeDataString(q)}");

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await response.ReadJsonAsync<Page<FamilyHit>>()).Items.Select(h => h.Name).ShouldBe([expected]);
    }

    [Test]
    public async Task Search_Empty_ReturnsRecentFirst()
    {
        await SeedAsync("Старое", @"ОВ\Старое.rfa", minutesAgo: 120);
        await SeedAsync("Новое", @"ОВ\Новое.rfa", minutesAgo: 1);
        await SeedAsync("Среднее", @"ОВ\Среднее.rfa", minutesAgo: 60);

        var names = await NamesAsync(await ClientAsync(UserRole.Designer), "q=");

        names.ShouldBe(["Новое", "Среднее", "Старое"]);
    }

    [Test]
    public async Task Search_PagesAndTotal()
    {
        for (var i = 0; i < 5; i++) await SeedAsync($"Клапан {i}", $@"ОВ\Клапан {i}.rfa", minutesAgo: i);

        var page = await (await ClientAsync(UserRole.Designer)).GetJsonAsync<Page<FamilyHit>>("/api/families/search?q=клапан&page=2&pageSize=2");

        page.Total.ShouldBe(5);
        page.Items.Select(h => h.Name).ShouldBe(["Клапан 2", "Клапан 3"]);
    }

    [Test]
    public async Task Search_Filters()
    {
        await SeedAsync("Клапан", @"ОВ\Клапан.rfa", content: Content(types: ["A"]), year: 2022);
        await SeedAsync("Клапан 2024", @"ОВ\Клапан 2024.rfa", content: Content(types: ["A"]), year: 2024);
        await SeedAsync("Балка", @"КР\Балка.rfa", content: Content("Каркас несущий", "Б1"));
        var designer = await ClientAsync(UserRole.Designer);

        (await NamesAsync(designer, "section=" + Uri.EscapeDataString("КР"))).ShouldBe(["Балка"]);
        (await NamesAsync(designer, "year=2024")).ShouldBe(["Клапан 2024"]);
        (await NamesAsync(designer, "category=" + Uri.EscapeDataString("Каркас несущий"))).ShouldBe(["Балка"]);
        (await NamesAsync(designer, "placement=OneLevelBased")).Length.ShouldBe(3);
    }

    [Test]
    public async Task Search_Designer_SeesOnlyApproved_AndInReviewOnRequest_NeverArchived()
    {
        await SeedAsync("Утверждено", @"ОВ\Утверждено.rfa");
        await SeedAsync("На проверке", @"_На проверке\На проверке.rfa", CardState.InReview);
        await SeedAsync("В архиве", @"ОВ\В архиве.rfa", CardState.Archived);
        var designer = await ClientAsync(UserRole.Designer);
        var viewer = await ClientAsync(UserRole.Viewer);

        (await NamesAsync(designer, "q=")).ShouldBe(["Утверждено"]);
        (await NamesAsync(designer, "unreviewed=true")).ShouldBe(["Утверждено", "На проверке"], ignoreOrder: true);
        (await NamesAsync(designer, "state=Archived")).ShouldBe(["Утверждено"]);
        (await NamesAsync(viewer, "q=")).ShouldBe(["Утверждено"]);
        (await NamesAsync(viewer, "state=InReview")).ShouldBe(["На проверке"]);
        (await NamesAsync(viewer, "state=Archived")).ShouldBe(["В архиве"]);
        (await NamesAsync(viewer, "unreviewed=true")).ShouldBe(["Утверждено", "На проверке"], ignoreOrder: true); // панель Revit координатора
    }

    [Test]
    public async Task Facets_CountOnlyVisible()
    {
        await SeedAsync("Клапан", @"ОВ\Клапан.rfa", content: Content(types: ["A"]));
        await SeedAsync("Клапан 2", @"ОВ\Клапан 2.rfa", content: Content(types: ["A"]), year: 2024);
        await SeedAsync("Балка", @"КР\Балка.rfa", content: Content("Каркас несущий", "Б1"));
        await SeedAsync("На проверке", @"_На проверке\ВК\На проверке.rfa", CardState.InReview, Content(types: ["A"]));
        await SeedAsync("В архиве", @"АР\В архиве.rfa", CardState.Archived);

        var facets = await (await ClientAsync(UserRole.Designer)).GetJsonAsync<FamilyFacets>("/api/families/facets");

        facets.Sections.Select(f => (f.Value, f.Count)).ShouldBe([("ОВ", 2), ("КР", 1)]);
        facets.Categories.Select(f => (f.Value, f.Count)).ShouldBe([("Арматура трубопроводов", 2), ("Каркас несущий", 1)]);
        facets.Years.Select(f => (f.Value, f.Count)).ShouldBe([("2022", 2), ("2024", 1)]);
        facets.Placements.Select(f => (f.Value, f.Count)).ShouldBe([("OneLevelBased", 3)]);
    }
}

public class FamiliesSearchIndexTests : ServerTest
{
    [Test]
    public async Task TrigramIndex_CreatedWhenExtensionAvailable()
    {
        var (available, indexed) = await DbAsync(async db =>
        {
            var connection = db.Database.GetDbConnection();
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT EXISTS (SELECT 1 FROM pg_available_extensions WHERE name = 'pg_trgm'), " +
                                  "EXISTS (SELECT 1 FROM pg_indexes WHERE indexname = 'ix_family_cards_search_text_trgm')";
            await using var reader = await command.ExecuteReaderAsync();
            await reader.ReadAsync();
            return (reader.GetBoolean(0), reader.GetBoolean(1));
        });

        indexed.ShouldBe(available);
        Console.WriteLine($"pg_trgm во встроенном PostgreSQL: {available}");
    }
}
