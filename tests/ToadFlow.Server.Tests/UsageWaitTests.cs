using System.Net;
using Shouldly;
using ToadFlow.Contracts;
using ToadFlow.Server.Tests.Infrastructure;
using static ToadFlow.Server.Tests.Infrastructure.UsageFixtures;

namespace ToadFlow.Server.Tests;

public class UsageWaitTests : ServerTest
{
    public sealed record Basis(int People, int Machines);

    public sealed record WaitRow(string Key, string Title, int Count, double MedianMs, double P90Ms, long TotalMs, int Failed, double?[] Trend);

    public sealed record Waits(Basis Basis, WaitRow[] Rows);

    public sealed record Ref(string Id, string Name);

    public sealed record MachineRef(string Key, string Name);

    public sealed record Case(Guid Id, string Kind, DateTimeOffset StartedAt, long DurationMs, string Outcome, Ref User, MachineRef Machine, string ModelName,
        string ModelPath, string? HostModel, int? FreeMemoryMb, long? FileSizeBytes);

    public sealed record Person(Guid Id, string Name, string[] Disciplines);

    public sealed record Filters(Person[] People, MachineRef[] Machines, string[] Disciplines, int[] RevitYears);

    private static DateTimeOffset Ago(int hours) => DateTimeOffset.UtcNow.AddHours(-hours);

    [Test]
    public async Task Waits_ByModel_MedianP90Total()
    {
        var designer = await ClientAsync(UserRole.Designer);
        await SendAsync(designer, [.. new[] { 10, 20, 30, 40, 100 }.Select(s => Operation(durationMs: s * 1000L, startedAt: Ago(s)))]);
        await SendAsync(designer, Operation(durationMs: 500_000, outcome: "Failed"), Operation(kind: "Sync", durationMs: 900_000));

        var waits = await (await ClientAsync(UserRole.Viewer)).GetJsonAsync<Waits>("/api/usage/waits?group=model&kind=Open");

        var row = waits.Rows.ShouldHaveSingleItem();
        row.Key.ShouldBe("rsn://srv01/проект/ар.rvt");
        row.Title.ShouldBe("АР.rvt");
        row.Count.ShouldBe(6);
        row.Failed.ShouldBe(1);
        row.MedianMs.ShouldBe(30000);
        row.P90Ms.ShouldBeGreaterThan(40000);
        row.P90Ms.ShouldBeLessThan(100000);
        row.TotalMs.ShouldBe(200000);
        row.Trend.Length.ShouldBe(5);
    }

    [Test]
    public async Task Waits_ByModel_NoKind_AllButLinks()
    {
        var designer = await ClientAsync(UserRole.Designer);
        await SendAsync(designer, Operation(durationMs: 1000), Operation(kind: "Sync", durationMs: 2000), Operation(kind: "Link", durationMs: 5000,
            hostModelPath: "RSN://srv01/Проект/АР.rvt"));

        var waits = await (await ClientAsync(UserRole.Viewer)).GetJsonAsync<Waits>("/api/usage/waits?group=model");

        waits.Rows.ShouldHaveSingleItem().TotalMs.ShouldBe(3000);
    }

    [Test]
    public async Task Waits_Filter_UserMachineYearDiscipline()
    {
        var ar = await ClientAsync(UserRole.Designer, "АР");
        var both = await ClientAsync(UserRole.Designer, "АР", "КР");
        var none = await ClientAsync(UserRole.Designer);
        await SendAsync(ar, Operation(durationMs: 1000, machine: "WS-AR"));
        await SendAsync(both, Operation(durationMs: 2000, machine: "WS-BOTH", revitYear: 2022));
        await SendAsync(none, Operation(durationMs: 4000, machine: "WS-NONE"));
        var viewer = await ClientAsync(UserRole.Viewer);

        async Task<long> Total(string query) => (await viewer.GetJsonAsync<Waits>("/api/usage/waits?group=model&" + query)).Rows.Sum(r => r.TotalMs);

        (await Total("")).ShouldBe(7000);
        (await Total("discipline=АР")).ShouldBe(3000);
        (await Total("discipline=КР")).ShouldBe(2000);
        (await Total("discipline=" + Uri.EscapeDataString("Без раздела"))).ShouldBe(4000);
        (await Total("user=" + await UserIdAsync(ar))).ShouldBe(1000);
        (await Total("machine=ws-none")).ShouldBe(4000);
        (await Total("revitYear=2022")).ShouldBe(2000);
    }

    [Test]
    public async Task Waits_Basis_CountsPeopleAndMachines()
    {
        var a = await ClientAsync(UserRole.Designer);
        var b = await ClientAsync(UserRole.Designer);
        await SendAsync(a, Operation(machine: "WS-1"), Operation(machine: "WS-2"));
        await SendAsync(b, Operation(machine: "WS-2"));

        var waits = await (await ClientAsync(UserRole.Viewer)).GetJsonAsync<Waits>("/api/usage/waits?group=machine");

        waits.Basis.ShouldBe(new Basis(2, 2));
        waits.Rows.Select(r => r.Key).ShouldBe(["ws-1", "ws-2"], ignoreOrder: true);
    }

    [Test]
    public async Task Waits_Period_Inclusive_ByServerZone()
    {
        var designer = await ClientAsync(UserRole.Designer);
        var from = Today.AddDays(-5);
        var to = Today.AddDays(-3);
        await SendAsync(designer,
            Operation(durationMs: 1, startedAt: StartOf(from).AddMinutes(-1)),
            Operation(durationMs: 10, startedAt: StartOf(from).AddMinutes(1)),
            Operation(durationMs: 100, startedAt: StartOf(to.AddDays(1)).AddMinutes(-1)),
            Operation(durationMs: 1000, startedAt: StartOf(to.AddDays(1)).AddMinutes(1)));

        var waits = await (await ClientAsync(UserRole.Viewer)).GetJsonAsync<Waits>($"/api/usage/waits?group=model&from={Day(from)}&to={Day(to)}");

        waits.Rows.ShouldHaveSingleItem().TotalMs.ShouldBe(110);
    }

    [Test]
    public async Task Waits_DefaultPeriod_Last30Days()
    {
        var designer = await ClientAsync(UserRole.Designer);
        await SendAsync(designer, Operation(durationMs: 10, startedAt: StartOf(Today.AddDays(-29)).AddMinutes(1)),
            Operation(durationMs: 1000, startedAt: StartOf(Today.AddDays(-29)).AddMinutes(-1)));

        var waits = await (await ClientAsync(UserRole.Viewer)).GetJsonAsync<Waits>("/api/usage/waits?group=model");

        waits.Rows.ShouldHaveSingleItem().TotalMs.ShouldBe(10);
    }

    [Test]
    public async Task Waits_BadDate_400()
    {
        var response = await (await ClientAsync(UserRole.Viewer)).GetAsync("/api/usage/waits?group=model&from=вчера");

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Test]
    public async Task Waits_ByServer_GroupsRevitServerHosts()
    {
        var designer = await ClientAsync(UserRole.Designer);
        await SendAsync(designer,
            Operation(modelPath: "RSN://SRV01/A/1.rvt", durationMs: 1000),
            Operation(modelPath: "RSN://srv01/B/2.rvt", durationMs: 2000),
            Operation(modelPath: "RSN://srv02/A/1.rvt", durationMs: 4000),
            Operation(modelPath: @"\\fs\p\3.rvt", durationMs: 8000));

        var waits = await (await ClientAsync(UserRole.Viewer)).GetJsonAsync<Waits>("/api/usage/waits?group=server");

        waits.Rows.Select(r => (r.Key, r.TotalMs)).ShouldBe([("srv01", 3000L), ("srv02", 4000L)], ignoreOrder: true);
    }

    [Test]
    public async Task Waits_Links_GroupedByLinkModel()
    {
        var designer = await ClientAsync(UserRole.Designer);
        await SendAsync(designer,
            Operation(kind: "Link", modelPath: "RSN://srv01/П/КР.rvt", hostModelPath: "RSN://srv01/П/АР.rvt", durationMs: 1000),
            Operation(kind: "Link", modelPath: "RSN://srv01/П/КР.rvt", hostModelPath: "RSN://srv01/П/ОВ.rvt", durationMs: 3000),
            Operation(kind: "Open", modelPath: "RSN://srv01/П/КР.rvt", durationMs: 50000));

        var waits = await (await ClientAsync(UserRole.Viewer)).GetJsonAsync<Waits>("/api/usage/waits?group=link&kind=Open");

        var row = waits.Rows.ShouldHaveSingleItem();
        row.Title.ShouldBe("КР.rvt");
        row.Count.ShouldBe(2);
        row.TotalMs.ShouldBe(4000);
    }

    [Test]
    public async Task Waits_ByUser_TitledByName()
    {
        var designer = await ClientAsync(UserRole.Designer);
        await SendAsync(designer, Operation());

        var waits = await (await ClientAsync(UserRole.Viewer)).GetJsonAsync<Waits>("/api/usage/waits?group=user");

        var row = waits.Rows.ShouldHaveSingleItem();
        row.Key.ShouldBe((await UserIdAsync(designer)).ToString());
        row.Title.ShouldBe("Тест Designer");
    }

    [Test]
    public async Task Cases_ReturnsNewestFirst_Max200()
    {
        var designer = await ClientAsync(UserRole.Designer);
        var items = Enumerable.Range(0, 210).Select(i => Operation(startedAt: Ago(1).AddMinutes(-i), durationMs: i, freeMemoryMb: 1234)).ToArray();
        await SendAsync(designer, items[..200]);
        await SendAsync(designer, items[200..]);
        await SendAsync(designer, Machine(name: Machine1));

        var cases = await (await ClientAsync(UserRole.Viewer)).GetJsonAsync<Case[]>(
            "/api/usage/waits/cases?group=model&key=" + Uri.EscapeDataString("rsn://srv01/проект/ар.rvt") + "&kind=Open");

        cases.Length.ShouldBe(200);
        cases[0].DurationMs.ShouldBe(0);
        cases.Select(c => c.StartedAt).ShouldBeInOrder(SortDirection.Descending);
        cases[0].User.Name.ShouldBe("Тест Designer");
        cases[0].Machine.ShouldBe(new MachineRef("ws-01", Machine1));
        cases[0].FreeMemoryMb.ShouldBe(1234);
    }

    [Test]
    public async Task Filters_ListsPeopleMachinesDisciplinesYears()
    {
        var designer = await ClientAsync(UserRole.Designer, "ОВ");
        await SendAsync(designer, Machine(name: "WS-9"), Session(machine: "WS-9", revitYear: 2023));
        var id = await UserIdAsync(designer);

        var filters = await (await ClientAsync(UserRole.Viewer)).GetJsonAsync<Filters>("/api/usage/filters");

        filters.People.ShouldContain(p => p.Id == id && p.Disciplines.SequenceEqual(new[] { "ОВ" }));
        filters.Machines.ShouldBe([new MachineRef("ws-9", "WS-9")]);
        filters.Disciplines.ShouldContain("ОВ");
        filters.Disciplines.Last().ShouldBe("Без раздела");
        filters.RevitYears.ShouldBe([2023]);
    }

    [Test]
    [Arguments("/api/usage/waits?group=model")]
    [Arguments("/api/usage/waits/cases?group=model&key=x")]
    [Arguments("/api/usage/filters")]
    public async Task Read_Designer_403(string url)
    {
        var response = await (await ClientAsync(UserRole.Designer)).GetAsync(url);

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Test]
    [Arguments("/api/usage/waits?group=model")]
    [Arguments("/api/usage/waits/cases?group=model&key=x")]
    [Arguments("/api/usage/filters")]
    public async Task Read_Viewer_200(string url)
    {
        var response = await (await ClientAsync(UserRole.Viewer)).GetAsync(url);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
    }
}
