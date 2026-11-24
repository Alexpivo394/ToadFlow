using System.Net;
using Shouldly;
using ToadFlow.Contracts;
using ToadFlow.Server.Tests.Infrastructure;
using static ToadFlow.Server.Tests.Infrastructure.UsageFixtures;

namespace ToadFlow.Server.Tests;

public class UsageCommandTests : ServerTest
{
    public sealed record Basis(int People, int Machines);

    public sealed record CommandRow(string Source, string CommandId, string Name, string? Tab, int Count, int People, int Failed, int Cancelled, double? AvgMs);

    public sealed record Unused(string CommandId, string Name, DateOnly? LastRunAt);

    public sealed record Commands(Basis Basis, bool RibbonOnly, CommandRow[] Rows, Unused[] Unused);

    public sealed record Week(DateOnly Start, int Count, int People);

    public sealed record User(Guid Id, string Name, int Count);

    public sealed record Card(string Name, Week[] Weeks, User[] Users);

    [Test]
    public async Task Commands_SumAcrossSessionsAndDays_PeopleDistinct()
    {
        var a = await ClientAsync(UserRole.Designer);
        var b = await ClientAsync(UserRole.Designer);
        var s1 = Guid.NewGuid();
        var s2 = Guid.NewGuid();
        await SendAsync(a, CommandDay(s1, Today, count: 3, failed: 1, durationMs: 3000), CommandDay(s1, Today.AddDays(-1), count: 2, durationMs: 2000),
            CommandDay(s2, Today, count: 1, cancelled: 1, durationMs: 1000));
        await SendAsync(b, CommandDay(Guid.NewGuid(), Today, count: 4, durationMs: 4000, machine: "WS-02"),
            CommandDay(Guid.NewGuid(), Today, commandId: "ID_OBJECTS_WALL", name: "Стена", source: "Revit", tab: "Архитектура", count: 7, durationMs: 0));

        var commands = await (await ClientAsync(UserRole.Viewer)).GetJsonAsync<Commands>("/api/usage/commands");

        commands.RibbonOnly.ShouldBeTrue();
        var export = commands.Rows.Single(r => r.CommandId == "Export");
        (export.Source, export.Name, export.Count, export.People, export.Failed, export.Cancelled).ShouldBe(("ToadTools", "Выгрузка", 10, 2, 1, 1));
        export.AvgMs.ShouldBe(1000);
        var wall = commands.Rows.Single(r => r.CommandId == "ID_OBJECTS_WALL");
        (wall.Source, wall.Tab, wall.Count, wall.People).ShouldBe(("Revit", "Архитектура", 7, 1));
        wall.AvgMs.ShouldBeNull();
        commands.Rows[0].CommandId.ShouldBe("Export");
        commands.Basis.ShouldBe(new Basis(2, 2));
    }

    [Test]
    public async Task Commands_SourceFilter()
    {
        var a = await ClientAsync(UserRole.Designer);
        await SendAsync(a, CommandDay(Guid.NewGuid(), Today), CommandDay(Guid.NewGuid(), Today, commandId: "ID_X", source: "Revit"));

        var commands = await (await ClientAsync(UserRole.Viewer)).GetJsonAsync<Commands>("/api/usage/commands?source=Revit");

        commands.Rows.ShouldHaveSingleItem().CommandId.ShouldBe("ID_X");
    }

    [Test]
    public async Task Commands_FilterByPersonAndDiscipline()
    {
        var ar = await ClientAsync(UserRole.Designer, "АР");
        var kr = await ClientAsync(UserRole.Designer, "КР");
        await SendAsync(ar, CommandDay(Guid.NewGuid(), Today, count: 2));
        await SendAsync(kr, CommandDay(Guid.NewGuid(), Today, count: 5));
        var viewer = await ClientAsync(UserRole.Viewer);

        (await viewer.GetJsonAsync<Commands>("/api/usage/commands?discipline=КР")).Rows.Single().Count.ShouldBe(5);
        (await viewer.GetJsonAsync<Commands>("/api/usage/commands?user=" + await UserIdAsync(ar))).Rows.Single().Count.ShouldBe(2);
    }

    [Test]
    public async Task Unused_ListsToadToolsWithoutRuns90Days()
    {
        var a = await ClientAsync(UserRole.Designer);
        await SendAsync(a,
            CommandDay(Guid.NewGuid(), Today.AddDays(-100), commandId: "Old", name: "Старая"),
            CommandDay(Guid.NewGuid(), Today.AddDays(-10), commandId: "Fresh", name: "Свежая"),
            CommandDay(Guid.NewGuid(), Today.AddDays(-100), commandId: "ID_OLD", source: "Revit"));

        var commands = await (await ClientAsync(UserRole.Viewer)).GetJsonAsync<Commands>($"/api/usage/commands?from={Day(Today)}");

        commands.Unused.ShouldBe([new Unused("Old", "Старая", Today.AddDays(-100))]);
    }

    [Test]
    public async Task Command_Card_WeeksAndUsers()
    {
        var a = await ClientAsync(UserRole.Designer);
        var b = await ClientAsync(UserRole.Designer);
        await SendAsync(a, CommandDay(Guid.NewGuid(), Today, count: 3), CommandDay(Guid.NewGuid(), Today.AddDays(-20), count: 2));
        await SendAsync(b, CommandDay(Guid.NewGuid(), Today, count: 1));

        var card = await (await ClientAsync(UserRole.Viewer)).GetJsonAsync<Card>("/api/usage/commands/ToadTools/Export");

        card.Name.ShouldBe("Выгрузка");
        card.Weeks.Length.ShouldBe(5);
        card.Weeks.Sum(w => w.Count).ShouldBe(6);
        card.Weeks.Last().People.ShouldBe(2);
        card.Users.Select(u => (u.Id, u.Count)).ShouldBe([(await UserIdAsync(a), 5), (await UserIdAsync(b), 1)]);
    }

    [Test]
    public async Task Command_Unknown_404()
    {
        var response = await (await ClientAsync(UserRole.Viewer)).GetAsync("/api/usage/commands/ToadTools/Nothing");

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Test]
    [Arguments("/api/usage/commands")]
    [Arguments("/api/usage/commands/ToadTools/Export")]
    public async Task Designer_403(string url)
    {
        (await (await ClientAsync(UserRole.Designer)).GetAsync(url)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }
}
