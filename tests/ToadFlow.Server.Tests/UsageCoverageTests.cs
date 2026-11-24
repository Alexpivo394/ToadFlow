using Microsoft.EntityFrameworkCore;
using Shouldly;
using ToadFlow.Contracts;
using ToadFlow.Server.Tests.Infrastructure;
using static ToadFlow.Server.Tests.Infrastructure.UsageFixtures;

namespace ToadFlow.Server.Tests;

public class UsageCoverageTests : ServerTest
{
    public sealed record CoverageUser(Guid Id, string Name, string Role, string[] Disciplines, string? Position);

    public sealed record CoverageRow(CoverageUser User, string State, string? ToadTools, bool Outdated, DateTimeOffset? LastSessionAt, string[] Machines);

    public sealed record Coverage(string MinToadTools, string? LatestToadTools, CoverageRow[] Rows);

    private Task<Coverage> CoverageAsync(string query = "") => ClientAsync(UserRole.Viewer).ContinueWith(t => t.Result.GetJsonAsync<Coverage>("/api/usage/coverage" + query)).Unwrap();

    [Test]
    public async Task Coverage_NeverActiveGone()
    {
        var never = await ClientAsync(UserRole.Designer);
        var active = await ClientAsync(UserRole.Designer);
        var gone = await ClientAsync(UserRole.Designer);
        await SendAsync(active, Session(startedAt: DateTimeOffset.UtcNow.AddDays(-1), machine: "WS-A"));
        await SendAsync(gone, Session(startedAt: DateTimeOffset.UtcNow.AddDays(-20), machine: "WS-G"));
        await SendAsync(active, Machine(name: "WS-A"));

        var coverage = await CoverageAsync();

        coverage.MinToadTools.ShouldBe("4.6.0");
        CoverageRow Row(Guid id) => coverage.Rows.Single(r => r.User.Id == id);
        Row(await UserIdAsync(never)).State.ShouldBe("Never");
        Row(await UserIdAsync(never)).LastSessionAt.ShouldBeNull();
        var activeRow = Row(await UserIdAsync(active));
        activeRow.State.ShouldBe("Active");
        activeRow.ToadTools.ShouldBe("4.6.0");
        activeRow.Machines.ShouldBe(["WS-A"]);
        Row(await UserIdAsync(gone)).State.ShouldBe("Gone");
        Row(await UserIdAsync(gone)).Machines.ShouldBe(["ws-g"]);
    }

    [Test]
    public async Task Coverage_Outdated_ComparesVersionsNumerically()
    {
        var old = await ClientAsync(UserRole.Designer);
        var fresh = await ClientAsync(UserRole.Designer);
        await SendAsync(old, Session(toadTools: "4.9.0"));
        await SendAsync(fresh, Session(toadTools: "4.10.0"));

        var coverage = await CoverageAsync();

        coverage.LatestToadTools.ShouldBe("4.10.0");
        coverage.Rows.Single(r => r.User.Id == UserIdAsync(old).Result).Outdated.ShouldBeTrue();
        coverage.Rows.Single(r => r.User.Id == UserIdAsync(fresh).Result).Outdated.ShouldBeFalse();
    }

    [Test]
    public async Task Coverage_SkipsDisabledAndPending()
    {
        var disabled = await ClientAsync(UserRole.Designer);
        var pending = await ClientAsync(UserRole.Designer);
        var disabledId = await UserIdAsync(disabled);
        var pendingId = await UserIdAsync(pending);
        await DbAsync(async db =>
        {
            await db.Users.Where(u => u.Id == disabledId).ExecuteUpdateAsync(u => u.SetProperty(x => x.IsDisabled, true));
            await db.Users.Where(u => u.Id == pendingId).ExecuteUpdateAsync(u => u.SetProperty(x => x.IsPending, true));
        });

        var coverage = await CoverageAsync();

        coverage.Rows.ShouldNotContain(r => r.User.Id == disabledId || r.User.Id == pendingId);
        coverage.Rows.ShouldContain(r => r.User.Role == "Admin");
    }

    [Test]
    public async Task Coverage_DisciplineFilter_PeriodIgnored()
    {
        var ar = await ClientAsync(UserRole.Designer, "АР");
        await ClientAsync(UserRole.Designer, "КР");
        await SendAsync(ar, Session(startedAt: DateTimeOffset.UtcNow.AddDays(-60)));

        var coverage = await CoverageAsync($"?discipline=АР&from={Day(Today)}&to={Day(Today)}");

        var row = coverage.Rows.ShouldHaveSingleItem();
        row.User.Disciplines.ShouldBe(["АР"]);
        row.State.ShouldBe("Gone");
    }
}
