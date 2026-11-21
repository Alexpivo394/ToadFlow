using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using ToadFlow.Contracts;
using ToadFlow.Server.Data;
using ToadFlow.Server.Families.Data;
using ToadFlow.Server.Families.Requests;
using ToadFlow.Server.Tests.Infrastructure;
using static ToadFlow.Server.Tests.Infrastructure.FamiliesFixtures;

namespace ToadFlow.Server.Tests;

/// <summary>Направления BIM: кому идут заявки. Настройка, выбор направления по разделу и профилю, координаторы.</summary>
public class FamiliesDirectionTests : ServerTest
{
    public FamiliesDirectionTests() => Services = WithoutScanLoop;

    private sealed record Me(Guid Id);

    private static async Task<Guid> IdAsync(HttpClient client) => (await client.GetJsonAsync<Me>("/api/profile")).Id;

    private static DirectionInput Direction(string name, string[] sections, string[] disciplines, params Guid[] coordinators) =>
        new(name, sections, disciplines, coordinators, $"подсказка {name}");

    private async Task<List<DirectionView>> SaveAsync(params DirectionInput[] directions)
    {
        var admin = await AdminAsync();
        var response = await admin.PutJsonAsync("/api/families/directions", directions);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        return await response.ReadJsonAsync<List<DirectionView>>();
    }

    [Test]
    public async Task Put_SavesAndListsForDesigner()
    {
        var ov = await IdAsync(await ClientAsync(UserRole.Operator));
        var saved = await SaveAsync(Direction("ОВ/ВК", ["Отопление", "Вентиляция"], ["ОВ", "ВК"], ov), Direction("АР/КР", ["Двери"], ["АР"]));

        saved.Select(d => d.Name).ShouldBe(["ОВ/ВК", "АР/КР"]);
        var designer = await ClientAsync(UserRole.Designer);
        var listed = await designer.GetJsonAsync<List<DirectionView>>("/api/families/directions");
        listed[0].Coordinators.Select(c => (c.Id, c.Name)).ShouldBe([(ov, "Тест Operator")]);
        listed[0].Sections.ShouldBe(["Отопление", "Вентиляция"]);
        listed[0].Disciplines.ShouldBe(["ОВ", "ВК"]);
        listed[0].Hint.ShouldBe("подсказка ОВ/ВК");
    }

    [Test]
    public async Task Put_AdminOnly_ValidatesNamesAndCoordinatorRoles()
    {
        var operatorClient = await ClientAsync(UserRole.Operator);
        (await operatorClient.PutJsonAsync("/api/families/directions", Array.Empty<DirectionInput>())).StatusCode.ShouldBe(HttpStatusCode.Forbidden);

        var admin = await AdminAsync();
        (await admin.PutJsonAsync("/api/families/directions", new[] { Direction(" ", [], []) })).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await admin.PutJsonAsync("/api/families/directions", new[] { Direction("ОВ", [], []), Direction("ов", [], []) })).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        var designer = await IdAsync(await ClientAsync(UserRole.Designer));
        (await admin.PutJsonAsync("/api/families/directions", new[] { Direction("ОВ", [], [], designer) })).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Test]
    public async Task Resolve_BySection()
    {
        var saved = await SaveAsync(Direction("ОВ/ВК", ["Отопление"], ["ОВ"]), Direction("АР/КР", ["Двери"], ["АР"]));
        var author = await IdAsync(await ClientAsync(UserRole.Designer, "ОВ"));

        var resolved = await DbAsync(db => Directions.ResolveAsync(db, "двери", author, CancellationToken.None));

        resolved.ShouldBe(saved[1].Id);
    }

    [Test]
    public async Task Resolve_ByProfileDiscipline_OnlyWhenSingle()
    {
        var saved = await SaveAsync(Direction("ОВ/ВК", [], ["ОВ", "ВК"]), Direction("АР/КР", [], ["АР", "КР"]));
        var single = await IdAsync(await ClientAsync(UserRole.Designer, "ОВ", "ВК"));
        var both = await IdAsync(await ClientAsync(UserRole.Designer, "ОВ", "АР"));

        (await DbAsync(db => Directions.ResolveAsync(db, "", single, CancellationToken.None))).ShouldBe(saved[0].Id);
        (await DbAsync(db => Directions.ResolveAsync(db, "", both, CancellationToken.None))).ShouldBeNull();
    }

    [Test]
    public async Task Resolve_None_ReturnsNull()
    {
        var author = await IdAsync(await ClientAsync(UserRole.Designer));
        (await DbAsync(db => Directions.ResolveAsync(db, "Двери", author, CancellationToken.None))).ShouldBeNull();
    }

    [Test]
    public async Task Coordinators_EmptyDirection_FallsBackToAllStaff()
    {
        var ov = await IdAsync(await ClientAsync(UserRole.Operator));
        var other = await IdAsync(await ClientAsync(UserRole.Operator));
        var saved = await SaveAsync(Direction("ОВ/ВК", [], [], ov), Direction("АР/КР", [], []));

        (await DbAsync(db => Directions.CoordinatorsAsync(db, saved[0].Id, CancellationToken.None))).ShouldBe([ov]);
        var fallback = await DbAsync(db => Directions.CoordinatorsAsync(db, saved[1].Id, CancellationToken.None));
        fallback.ShouldContain(ov);
        fallback.ShouldContain(other);
        (await DbAsync(db => Directions.CoordinatorsAsync(db, null, CancellationToken.None))).ShouldContain(other);
    }

    [Test]
    public async Task Uncovered_ListsSectionsOutsideDirections()
    {
        await DbAsync(async db =>
        {
            foreach (var (name, section) in new[] { ("Дверь", "Двери"), ("Клапан", "Отопление"), ("Окно", "Окна") })
            {
                var card = Card(name, $@"{section}\{name}.rfa", CardState.Approved, DateTimeOffset.UtcNow);
                card.Section = section;
                db.Set<FamilyCard>().Add(card);
            }

            await db.SaveChangesAsync();
        });
        await SaveAsync(Direction("АР/КР", ["Двери", "Окна"], []));

        var coordinator = await ClientAsync(UserRole.Operator);
        (await coordinator.GetJsonAsync<List<string>>("/api/families/directions/uncovered")).ShouldBe(["Отопление"]);
    }
}
