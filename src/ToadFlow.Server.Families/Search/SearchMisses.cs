using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using ToadFlow.Server.Auth;
using ToadFlow.Server.Data;
using ToadFlow.Server.Families.Requests;

namespace ToadFlow.Server.Families.Search;

/// <summary>Поиск без результата: что искали в каталоге или панели Revit и не нашли.</summary>
public sealed class FamilySearchMiss
{
    public Guid Id { get; set; }

    public Guid UserId { get; set; }

    /// <summary>Запрос в нижнем регистре, без лишних пробелов.</summary>
    public string Query { get; set; } = "";

    /// <summary><c>web</c> или <c>panel</c>.</summary>
    public string Source { get; set; } = "web";

    public DateTimeOffset At { get; set; }
}

/// <param name="RequestId">Открытая заявка, в которой есть все слова запроса.</param>
public sealed record MissRow(string Query, int Count, int Users, DateTimeOffset LastAt, Guid? RequestId, int? RequestNumber);

/// <summary>
///     Журнал поисков без результата. Набор по буквам не засоряет его: запрос того же человека в пределах 2 минут, продолжающий прежний
///     (или прежний продолжает его), заменяет прежнюю запись.
/// </summary>
public static class SearchMisses
{
    public const int MinLength = 3;
    private const int MaxLength = 200;
    private const int KeepDays = 180;
    private static readonly TimeSpan GlueWindow = TimeSpan.FromMinutes(2);

    public static async Task RecordAsync(ToadFlowDb db, Guid userId, string? q, string? source, DateTimeOffset now, CancellationToken ct)
    {
        var query = string.Join(' ', (q ?? "").ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        if (query.Length < MinLength) return;
        if (query.Length > MaxLength) query = query[..MaxLength];

        var since = now - GlueWindow;
        var last = await db.Set<FamilySearchMiss>().Where(m => m.UserId == userId && m.At >= since).OrderByDescending(m => m.At).FirstOrDefaultAsync(ct);
        if (last is not null && (query.StartsWith(last.Query, StringComparison.Ordinal) || last.Query.StartsWith(query, StringComparison.Ordinal)))
        {
            last.Query = query;
            last.At = now;
        }
        else
            db.Set<FamilySearchMiss>().Add(new FamilySearchMiss
                { Id = Guid.CreateVersion7(), UserId = userId, Query = query, Source = source == "panel" ? "panel" : "web", At = now });

        await db.SaveChangesAsync(ct);
    }

    public static Task<int> CleanAsync(ToadFlowDb db, DateTimeOffset now, CancellationToken ct) =>
        db.Set<FamilySearchMiss>().Where(m => m.At < now.AddDays(-KeepDays)).ExecuteDeleteAsync(ct);

    public static void MapSearchMisses(this RouteGroupBuilder families) =>
        families.MapGet("/misses", ListAsync).RequireAuthorization(Policies.Operator);

    /// <summary>Частые выше; <c>days</c> — за сколько дней (по умолчанию 30).</summary>
    public static async Task<List<MissRow>> ListAsync(ToadFlowDb db, TimeProvider time, int? days, CancellationToken ct)
    {
        var since = time.GetUtcNow().AddDays(-Math.Clamp(days ?? 30, 1, KeepDays));
        var rows = await db.Set<FamilySearchMiss>().AsNoTracking().Where(m => m.At >= since)
            .GroupBy(m => m.Query)
            .Select(g => new { Query = g.Key, Count = g.Count(), Users = g.Select(m => m.UserId).Distinct().Count(), LastAt = g.Max(m => m.At) })
            .OrderByDescending(g => g.Count).ThenByDescending(g => g.LastAt).Take(100).ToListAsync(ct);
        var open = FamilyRequest.OpenStates;
        var requests = await db.Set<FamilyRequest>().AsNoTracking().Where(r => open.Contains(r.State))
            .Select(r => new { r.Id, r.Number, r.SearchText }).ToListAsync(ct);
        return rows.Select(row =>
        {
            var words = row.Query.Split(' ');
            var request = requests.FirstOrDefault(r => words.All(w => r.SearchText.Contains(w)));
            return new MissRow(row.Query, row.Count, row.Users, row.LastAt, request?.Id, request?.Number);
        }).ToList();
    }
}
