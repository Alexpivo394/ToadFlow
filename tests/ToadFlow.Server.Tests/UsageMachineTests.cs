using System.Net;
using Shouldly;
using ToadFlow.Contracts;
using ToadFlow.Server.Tests.Infrastructure;
using static ToadFlow.Server.Tests.Infrastructure.UsageFixtures;

namespace ToadFlow.Server.Tests;

public class UsageMachineTests : ServerTest
{
    public sealed record Basis(int People, int Machines);

    public sealed record Person(Guid Id, string Name);

    public sealed record MachineRow(string Key, string Name, string? Cpu, int? Cores, int? RamMb, string? Gpu, int? GpuMemoryMb, string DiskKind, int? DiskFreeGb,
        string? Os, int[] RevitYears, string? ToadTools, DateTimeOffset? LastSeenAt, Person? LastUser, double? OpenMedianMs, double? SyncMedianMs, int Sessions,
        int Abnormal, double ActiveHours);

    public sealed record Machines(Basis Basis, MachineRow[] Rows);

    public sealed record CardUser(Guid Id, string Name, int Sessions);

    public sealed record AbnormalSession(Guid Id, DateTimeOffset StartedAt, DateTimeOffset SeenAt, Person User);

    public sealed record Week(DateOnly Start, double? OpenMedianMs, double? SyncMedianMs);

    public sealed record Card(string Key, string Name, string? Cpu, int? RamMb, Person? LastUser, double? OpenMedianMs, int Sessions, int Abnormal,
        CardUser[] Users, AbnormalSession[] AbnormalSessions, Week[] Weeks);

    private static DateTimeOffset Ago(int hours) => DateTimeOffset.UtcNow.AddHours(-hours);

    private async Task SeedAsync(HttpClient a, HttpClient b)
    {
        var s1 = Guid.NewGuid();
        var s2 = Guid.NewGuid();
        var s3 = Guid.NewGuid();
        await SendAsync(a, Machine(name: "WS-01", ramMb: 16384), Machine(name: "WS-02"),
            Session(s1, machine: "WS-01", startedAt: Ago(50), seenAt: Ago(48), end: "Abnormal"),
            Session(s2, machine: "WS-01", startedAt: Ago(10), seenAt: Ago(9), end: "Normal"),
            SessionDay(s2, Today, 3600, machine: "WS-01"), SessionDay(s1, Today.AddDays(-2), 1800, machine: "WS-01"),
            Operation(machine: "WS-01", durationMs: 10_000), Operation(machine: "WS-01", durationMs: 30_000), Operation(machine: "WS-01", durationMs: 50_000),
            Operation(machine: "WS-01", kind: "Sync", durationMs: 7_000),
            Operation(machine: "WS-02", durationMs: 1_000));
        await SendAsync(b, Session(s3, machine: "WS-01", startedAt: Ago(5), seenAt: Ago(4), end: "Abnormal"));
    }

    [Test]
    public async Task Machines_Row_MediansAndAbnormalCount()
    {
        await SeedAsync(await ClientAsync(UserRole.Designer), await ClientAsync(UserRole.Designer));

        var machines = await (await ClientAsync(UserRole.Viewer)).GetJsonAsync<Machines>("/api/usage/machines");

        machines.Rows.Select(r => r.Key).ShouldBe(["ws-01", "ws-02"]);
        var row = machines.Rows[0];
        row.Name.ShouldBe("WS-01");
        row.RamMb.ShouldBe(16384);
        row.DiskKind.ShouldBe("Ssd");
        row.RevitYears.ShouldBe([2022, 2024]);
        row.OpenMedianMs.ShouldBe(30_000);
        row.SyncMedianMs.ShouldBe(7_000);
        row.Sessions.ShouldBe(3);
        row.Abnormal.ShouldBe(2);
        row.ActiveHours.ShouldBe(1.5);
        row.LastUser.ShouldNotBeNull();
        machines.Rows[1].Sessions.ShouldBe(0);
        machines.Basis.ShouldBe(new Basis(2, 2));
    }

    [Test]
    public async Task Machines_UserFilter_OnlyTheirMachines()
    {
        var a = await ClientAsync(UserRole.Designer);
        var b = await ClientAsync(UserRole.Designer);
        await SeedAsync(a, b);
        await SendAsync(b, Session(machine: "WS-03"));

        var machines = await (await ClientAsync(UserRole.Viewer)).GetJsonAsync<Machines>("/api/usage/machines?user=" + await UserIdAsync(b));

        machines.Rows.Select(r => r.Key).ShouldBe(["ws-01", "ws-03"], ignoreOrder: true);
        machines.Rows.Single(r => r.Key == "ws-01").Sessions.ShouldBe(1);
    }

    [Test]
    public async Task Machine_Card_UsersAndAbnormalList()
    {
        var a = await ClientAsync(UserRole.Designer);
        var b = await ClientAsync(UserRole.Designer);
        await SeedAsync(a, b);

        var card = await (await ClientAsync(UserRole.Viewer)).GetJsonAsync<Card>("/api/usage/machines/ws-01");

        card.Name.ShouldBe("WS-01");
        card.Users.Select(u => (u.Id, u.Sessions)).ShouldBe([(await UserIdAsync(a), 2), (await UserIdAsync(b), 1)], ignoreOrder: true);
        card.AbnormalSessions.Length.ShouldBe(2);
        card.AbnormalSessions.Select(s => s.StartedAt).ShouldBeInOrder(SortDirection.Descending);
        card.AbnormalSessions[0].User.Id.ShouldBe(await UserIdAsync(b));
        card.Weeks.Length.ShouldBe(5);
        card.Weeks.Last().OpenMedianMs.ShouldBe(30_000);
    }

    [Test]
    public async Task Machine_Unknown_404()
    {
        var response = await (await ClientAsync(UserRole.Viewer)).GetAsync("/api/usage/machines/nobody");

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Test]
    [Arguments("/api/usage/machines")]
    [Arguments("/api/usage/machines/ws-01")]
    [Arguments("/api/usage/coverage")]
    public async Task Designer_403(string url)
    {
        var response = await (await ClientAsync(UserRole.Designer)).GetAsync(url);

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }
}
