using System.Globalization;
using Microsoft.EntityFrameworkCore;
using ToadFlow.Server.Usage.Data;

namespace ToadFlow.Server.Usage.Query;

public sealed record CoverageUser(Guid Id, string Name, string Role, string[] Disciplines, string? Position);

/// <param name="State"><c>Never</c> — данных от ToadTools не было; <c>Active</c> — на связи; <c>Gone</c> — последняя отметка старше <see cref="CoverageEndpoints.GoneDays" /> дней.</param>
/// <param name="Outdated">Версия ToadTools человека ниже самой новой за последние <see cref="CoverageEndpoints.LatestDays" /> дней.</param>
public sealed record CoverageRow(CoverageUser User, string State, string? ToadTools, bool Outdated, DateTimeOffset? LastSessionAt, string[] Machines);

/// <param name="MinToadTools">С какой версии ToadTools присылает данные.</param>
public sealed record CoverageView(string MinToadTools, string? LatestToadTools, IReadOnlyList<CoverageRow> Rows);

/// <summary>Экран «Охват»: все действующие учётки и есть ли от них данные ToadTools. Период фильтра не действует, только раздел.</summary>
public static class CoverageEndpoints
{
    public const string MinToadTools = "4.6.0";
    public const int GoneDays = 14;
    public const int LatestDays = 30;

    private static readonly StringComparer Russian = StringComparer.Create(CultureInfo.GetCultureInfo("ru-RU"), false);

    public static void MapUsageCoverage(this RouteGroupBuilder usage) => usage.MapGet("/coverage", CoverageAsync);

    private static async Task<CoverageView> CoverageAsync(HttpRequest request, UsageQuery query, CancellationToken ct)
    {
        var db = query.Db;
        var now = query.Now;
        var filter = await query.FilterAsync(request, ct);
        var users = await db.Users.AsNoTracking().Where(u => !u.IsDisabled && !u.IsPending).ToListAsync(ct);
        if (filter.Discipline is { } discipline)
            users = users.Where(u => discipline == UsageFilter.NoDiscipline ? u.Disciplines.Length == 0 : u.Disciplines.Contains(discipline)).ToList();

        var sessions = await db.Set<UsageSession>().AsNoTracking()
            .Select(s => new { s.UserId, s.SeenAt, s.ToadTools, s.MachineKey }).ToListAsync(ct);
        var machines = await query.MachinesAsync(ct);
        var latest = sessions.Where(s => s.SeenAt >= now.AddDays(-LatestDays)).Select(s => s.ToadTools).Where(v => v is not null)
            .MaxBy(v => Parse(v))?.Trim();
        var latestVersion = Parse(latest);

        var byUser = sessions.ToLookup(s => s.UserId);
        var rows = users.Select(user =>
        {
            var own = byUser[user.Id].OrderByDescending(s => s.SeenAt).ToList();
            var view = new CoverageUser(user.Id, user.DisplayName, user.Role.ToString(), user.Disciplines, user.Position);
            if (own.Count == 0) return new CoverageRow(view, "Never", null, false, null, []);

            var last = own[0];
            var version = Parse(last.ToadTools);
            var state = last.SeenAt < now.AddDays(-GoneDays) ? "Gone" : "Active";
            var outdated = version is not null && latestVersion is not null && version < latestVersion;
            var names = own.Select(s => s.MachineKey).Distinct().Select(k => machines.GetValueOrDefault(k, k)).Order(Russian).ToArray();
            return new CoverageRow(view, state, last.ToadTools, outdated, last.SeenAt, names);
        }).OrderBy(r => r.User.Name, Russian).ToList();

        return new CoverageView(MinToadTools, latest, rows);
    }

    /// <summary>Версия ToadTools числами: «4.10.0» новее «4.9.0»; хвост после «-» или «+» не учитывается.</summary>
    internal static Version? Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var core = text.Trim().Split('-', '+')[0];
        return Version.TryParse(core, out var version) ? version : null;
    }
}
