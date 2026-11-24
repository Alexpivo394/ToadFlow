using System.Net;
using Shouldly;
using ToadFlow.Contracts;
using ToadFlow.Server.Tests.Infrastructure;
using static ToadFlow.Server.Tests.Infrastructure.UsageFixtures;

namespace ToadFlow.Server.Tests;

public class UsageSummaryTests : ServerTest
{
    public sealed record Basis(int People, int Machines);

    public sealed record Totals(long WaitMs, int AbnormalSessions, int People, int ToadToolsRuns);

    public sealed record DayWait(DateOnly Day, long OpenMs, long LinkMs, long SyncMs, long SaveMs);

    public sealed record SlowModel(string Key, string Title, double MedianMs, int Count);

    public sealed record SlowMachine(string Key, string Name, long TotalMs);

    public sealed record CrashingMachine(string Key, string Name, int Abnormal);

    public sealed record Summary(Basis Basis, Totals Totals, Totals Previous, DayWait[] Days, SlowModel[] SlowModels, SlowMachine[] SlowMachines,
        CrashingMachine[] CrashingMachines);

    public sealed record PersonUser(Guid Id, string Name, string Role, string? Position, string[] Disciplines);

    public sealed record MachineRef(string Key, string Name);

    public sealed record ActivityDay(DateOnly Day, int ActiveSeconds, int Sessions);

    public sealed record WaitKind(string Kind, int Count, double? MedianMs);

    public sealed record Waits(long TotalMs, WaitKind[] Rows);

    public sealed record PersonCommand(string Source, string CommandId, string Name, int Count);

    public sealed record SpaceVisits(string Space, int Visits);

    public sealed record AreaCalls(string Area, int Calls);

    public sealed record Person(PersonUser User, MachineRef[] Machines, ActivityDay[] Days, Waits Waits, PersonCommand[] Commands, SpaceVisits[] Web,
        AreaCalls[] Plugins);

    private static DateTimeOffset At(DateOnly day, int hour = 10) => StartOf(day).AddHours(hour);

    [Test]
    public async Task Summary_Totals_And_PreviousPeriod()
    {
        var a = await ClientAsync(UserRole.Designer);
        var b = await ClientAsync(UserRole.Designer);
        var from = Today.AddDays(-6);
        await SendAsync(a,
            Operation(durationMs: 1000, startedAt: At(Today.AddDays(-1))), Operation(kind: "Sync", durationMs: 2000, startedAt: At(Today.AddDays(-2))),
            Operation(durationMs: 99_000, outcome: "Failed", startedAt: At(Today.AddDays(-1))),
            Operation(durationMs: 500, startedAt: At(from.AddDays(-3))),
            Session(startedAt: At(Today.AddDays(-1)), end: "Abnormal"), Session(startedAt: At(from.AddDays(-2)), end: "Abnormal"),
            CommandDay(Guid.NewGuid(), Today, count: 4), CommandDay(Guid.NewGuid(), Today, commandId: "ID_X", source: "Revit", count: 50));
        await SendAsync(b, Session(startedAt: At(Today.AddDays(-3))));

        var summary = await (await ClientAsync(UserRole.Viewer)).GetJsonAsync<Summary>($"/api/usage/summary?from={Day(from)}");

        summary.Totals.ShouldBe(new Totals(3000, 1, 2, 4));
        summary.Previous.ShouldBe(new Totals(500, 1, 1, 0));
    }

    [Test]
    public async Task Summary_Days_SplitByKind()
    {
        var a = await ClientAsync(UserRole.Designer);
        var day = Today.AddDays(-1);
        await SendAsync(a, Operation(durationMs: 1000, startedAt: At(day)), Operation(kind: "Link", durationMs: 2000, startedAt: At(day)),
            Operation(kind: "Sync", durationMs: 4000, startedAt: At(day)), Operation(kind: "Reload", durationMs: 8000, startedAt: At(day)),
            Operation(kind: "Save", durationMs: 16000, startedAt: At(day)));

        var summary = await (await ClientAsync(UserRole.Viewer)).GetJsonAsync<Summary>($"/api/usage/summary?from={Day(Today.AddDays(-6))}");

        summary.Days.Length.ShouldBe(7);
        summary.Days.Single(d => d.Day == day).ShouldBe(new DayWait(day, 1000, 2000, 12000, 16000));
        summary.Days.Where(d => d.Day != day).ShouldAllBe(d => d.OpenMs == 0 && d.SyncMs == 0);
    }

    [Test]
    public async Task Summary_Top5_Sorted()
    {
        var a = await ClientAsync(UserRole.Designer);
        var items = Enumerable.Range(1, 7).SelectMany(i => new[]
        {
            Operation(modelPath: $"RSN://srv/П/{i}.rvt", durationMs: i * 1000, machine: $"WS-{i}"),
            Session(machine: $"WS-{i}", end: i % 2 == 0 ? "Abnormal" : "Normal")
        }).ToArray();
        await SendAsync(a, items);

        var summary = await (await ClientAsync(UserRole.Viewer)).GetJsonAsync<Summary>("/api/usage/summary");

        summary.SlowModels.Select(m => m.Title).ShouldBe(["7.rvt", "6.rvt", "5.rvt", "4.rvt", "3.rvt"]);
        summary.SlowMachines.Select(m => m.Key).ShouldBe(["ws-7", "ws-6", "ws-5", "ws-4", "ws-3"]);
        summary.CrashingMachines.Select(m => m.Key).ShouldBe(["ws-2", "ws-4", "ws-6"], ignoreOrder: true);
    }

    [Test]
    public async Task Person_Aggregates()
    {
        var a = await ClientAsync(UserRole.Designer, "АР");
        var session = Guid.NewGuid();
        await SendAsync(a, Machine(name: "WS-01"), Session(session, startedAt: At(Today.AddDays(-1))), SessionDay(session, Today.AddDays(-1), 3600),
            Operation(durationMs: 1000), Operation(durationMs: 3000), Operation(kind: "Sync", durationMs: 5000),
            CommandDay(session, Today, count: 2), CommandDay(session, Today, commandId: "ID_X", name: "Стена", source: "Revit", count: 9));
        await a.PostJsonAsync("/api/usage/pageview", new { space = "clash", page = "/clash/my" });
        var id = await UserIdAsync(a);

        var person = await (await ClientAsync(UserRole.Viewer)).GetJsonAsync<Person>($"/api/usage/people/{id}");

        person.User.Disciplines.ShouldBe(["АР"]);
        person.Machines.ShouldBe([new MachineRef("ws-01", "WS-01")]);
        person.Days.ShouldContain(new ActivityDay(Today.AddDays(-1), 3600, 1));
        person.Waits.TotalMs.ShouldBe(9000);
        person.Waits.Rows.Single(r => r.Kind == "Open").ShouldBe(new WaitKind("Open", 2, 2000));
        person.Commands.Select(c => (c.CommandId, c.Count)).ShouldBe([("ID_X", 9), ("Export", 2)]);
        person.Web.ShouldBe([new SpaceVisits("clash", 1)]);
    }

    [Test]
    public async Task Person_Unknown_404()
    {
        (await (await ClientAsync(UserRole.Viewer)).GetAsync($"/api/usage/people/{Guid.NewGuid()}")).StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Test]
    [Arguments("/api/usage/summary")]
    [Arguments("/api/usage/people/00000000-0000-0000-0000-000000000001")]
    public async Task Designer_403(string url)
    {
        (await (await ClientAsync(UserRole.Designer)).GetAsync(url)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }
}
