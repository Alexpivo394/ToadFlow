using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using ToadFlow.Contracts;
using ToadFlow.Server.Tests.Infrastructure;
using ToadFlow.Server.Usage.Data;
using ToadFlow.Server.Usage.Maintenance;
using ToadFlow.Server.Usage.Settings;
using static ToadFlow.Server.Tests.Infrastructure.UsageFixtures;

namespace ToadFlow.Server.Tests;

public class UsageSettingsTests : ServerTest
{
    [Test]
    public async Task Settings_Default13()
    {
        var settings = await (await ClientAsync(UserRole.Viewer)).GetJsonAsync<UsageSettings>("/api/usage/settings");

        settings.ShouldBe(new UsageSettings(13));
    }

    [Test]
    public async Task Settings_Put_Admin_Ok_Operator403_Viewer403()
    {
        (await (await ClientAsync(UserRole.Viewer)).PutJsonAsync("/api/usage/settings", new UsageSettings(24))).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await (await ClientAsync(UserRole.Operator)).PutJsonAsync("/api/usage/settings", new UsageSettings(24))).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        var admin = await AdminAsync();

        (await admin.PutJsonAsync("/api/usage/settings", new UsageSettings(24))).StatusCode.ShouldBe(HttpStatusCode.OK);

        (await admin.GetJsonAsync<UsageSettings>("/api/usage/settings")).RetentionMonths.ShouldBe(24);
        await EventuallyAsync(() => DbAsync(db => db.SystemEvents.AnyAsync(e => e.Text.Contains("Аналитика") && e.Text.Contains("24"))));
    }

    [Test]
    [Arguments(0)]
    [Arguments(121)]
    public async Task Settings_OutOfRange_400(int months)
    {
        var response = await (await AdminAsync()).PutJsonAsync("/api/usage/settings", new UsageSettings(months));

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync()).ShouldContain("от 1 до 120");
    }

    [Test]
    public async Task Settings_Designer_403()
    {
        (await (await ClientAsync(UserRole.Designer)).GetAsync("/api/usage/settings")).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Test]
    public async Task Cleaner_DeletesOlderThanRetention_KeepsNewer()
    {
        var designer = await ClientAsync(UserRole.Designer);
        var old = DateTimeOffset.UtcNow.AddMonths(-14);
        var fresh = DateTimeOffset.UtcNow.AddDays(-2);
        var oldDay = DateOnly.FromDateTime(old.UtcDateTime);
        var freshDay = DateOnly.FromDateTime(fresh.UtcDateTime);
        var oldSession = Guid.NewGuid();
        var freshSession = Guid.NewGuid();
        await SendAsync(designer,
            Machine(name: "OLD", at: old), Machine(name: "FRESH", at: fresh),
            Session(oldSession, startedAt: old, machine: "OLD"), Session(freshSession, startedAt: fresh, machine: "FRESH"),
            SessionDay(oldSession, oldDay, 60), SessionDay(freshSession, freshDay, 60),
            Operation(startedAt: old), Operation(startedAt: fresh),
            CommandDay(oldSession, oldDay), CommandDay(freshSession, freshDay));
        var userId = await UserIdAsync(designer);
        await DbAsync(async db =>
        {
            db.Set<UsageWebDay>().AddRange(new UsageWebDay { UserId = userId, Day = oldDay, Space = "a", Page = "/a", Count = 1 },
                new UsageWebDay { UserId = userId, Day = freshDay, Space = "a", Page = "/a", Count = 1 });
            db.Set<UsagePluginDay>().AddRange(new UsagePluginDay { UserId = userId, Day = oldDay, Area = "clash", Count = 1 },
                new UsagePluginDay { UserId = userId, Day = freshDay, Area = "clash", Count = 1 });
            await db.SaveChangesAsync();
        });

        await Server.Services.GetRequiredService<UsageCleaner>().CleanAsync(CancellationToken.None);

        await DbAsync(async db =>
        {
            (await db.Set<UsageMachine>().Select(m => m.Key).ToListAsync()).ShouldBe(["fresh"]);
            (await db.Set<UsageSession>().Select(s => s.Id).ToListAsync()).ShouldBe([freshSession]);
            (await db.Set<UsageSessionDay>().Select(d => d.Day).ToListAsync()).ShouldBe([freshDay]);
            (await db.Set<UsageOperation>().CountAsync()).ShouldBe(1);
            (await db.Set<UsageCommandDay>().Select(c => c.Day).ToListAsync()).ShouldBe([freshDay]);
            (await db.Set<UsageWebDay>().Select(c => c.Day).ToListAsync()).ShouldBe([freshDay]);
            (await db.Set<UsagePluginDay>().Select(c => c.Day).ToListAsync()).ShouldBe([freshDay]);
            return true;
        });
    }
}

public class UsageOverviewTests : ServerTest
{
    public sealed record Tile(string Module, string Value, string Label, string[] Lines, string Link, double[]? Series);

    [Test]
    public async Task Tile_CountsPeopleWaitAbnormal()
    {
        var a = await ClientAsync(UserRole.Designer);
        var b = await ClientAsync(UserRole.Designer);
        var session = Guid.NewGuid();
        await SendAsync(a, Session(session, startedAt: DateTimeOffset.UtcNow.AddDays(-1), end: "Abnormal"), SessionDay(session, Today, 600),
            Operation(durationMs: 2 * 3_600_000, startedAt: DateTimeOffset.UtcNow.AddDays(-2)), Operation(durationMs: 3_600_000, startedAt: DateTimeOffset.UtcNow.AddDays(-9)),
            // Связь внутри открытия — уже в его двух часах.
            Operation(kind: "Link", durationMs: 3_600_000, startedAt: DateTimeOffset.UtcNow.AddDays(-2), modelPath: "RSN://srv01/Проект/КР.rvt",
                hostModelPath: "RSN://srv01/Проект/АР.rvt"));
        await SendAsync(b, Session(startedAt: DateTimeOffset.UtcNow.AddDays(-3)), Session(startedAt: DateTimeOffset.UtcNow.AddDays(-20)));

        var overview = JsonDocument.Parse(await (await ClientAsync(UserRole.Viewer)).GetStringAsync("/api/overview")).RootElement;
        var tile = overview.GetProperty("tiles").EnumerateArray().Single(t => t.GetProperty("module").GetString() == "usage").Deserialize<Tile>(TestJson.Options)!;

        tile.Value.ShouldBe("2");
        tile.Label.ShouldBe("на связи за неделю");
        tile.Lines.ShouldBe(["ожидание: 2 ч за 7 дней", "аварийных сессий: 1"]);
        tile.Link.ShouldBe("/usage");
        tile.Series!.Length.ShouldBe(14);
        tile.Series.Last().ShouldBe(1);
    }

    [Test]
    public async Task Tile_HiddenFromDesigner()
    {
        (await (await ClientAsync(UserRole.Designer)).GetAsync("/api/overview")).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Test]
    public async Task Overview_NoAttentionFromUsage()
    {
        var overview = JsonDocument.Parse(await (await AdminAsync()).GetStringAsync("/api/overview")).RootElement;

        overview.GetProperty("attention").EnumerateArray().ShouldNotContain(i => i.GetProperty("module").ToString() == "usage");
    }
}
