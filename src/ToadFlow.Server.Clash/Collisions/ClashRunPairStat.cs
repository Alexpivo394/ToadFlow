using Microsoft.EntityFrameworkCore;
using ToadFlow.Server.Clash.Engine;
using ToadFlow.Server.Data;
using ToadFlow.Server.Settings;

namespace ToadFlow.Server.Clash.Collisions;

/// <summary>
///     Счётчики утверждённого прогона по паре разделов — пишутся при сверке, по ним сравниваются прогоны (находки прогона после сверки
///     не хранятся). Строка с пустой парой (<see cref="Marker" />) пишется всегда: по ней видно, что прогон сверялся после обновления.
/// </summary>
public sealed class ClashRunPairStat
{
    /// <summary>Пара строки-отметки «счётчики есть».</summary>
    public const string Marker = "";

    public Guid RunId { get; set; }
    public Guid PartId { get; set; }

    /// <summary>Пара разделов, как <see cref="ClashCollision.DisciplinePair" />.</summary>
    public string DisciplinePair { get; set; } = "";

    /// <summary>Находки прогона этой пары.</summary>
    public int Found { get; set; }

    public int New { get; set; }
    public int Returned { get; set; }
    public int Fixed { get; set; }
    public int GoneByRules { get; set; }
}

/// <param name="FoundA">Найдено прогоном A (раньше по времени).</param>
/// <param name="FoundB">Найдено прогоном B.</param>
/// <param name="New">Появилось в прогонах после A до B включительно; так же остальные.</param>
public sealed record RunCompareRow(string Pair, int FoundA, int FoundB, int New, int Returned, int Fixed, int GoneByRules);

/// <param name="RunA">Прогон раньше по времени.</param>
public sealed record RunCompare(Guid RunA, Guid RunB, IReadOnlyList<RunCompareRow> Rows);

/// <summary>Сравнение двух утверждённых прогонов части по счётчикам пар разделов.</summary>
public static class RunComparison
{
    public static async Task<IResult> CompareAsync(Guid id, Guid a, Guid b, ToadFlowDb db, ISettingsStore general, CancellationToken ct)
    {
        var runs = await db.Set<EngineRun>().AsNoTracking()
            .Where(r => r.PartId == id && r.Kind == EngineRunKind.Approved && r.State == EngineRunState.Succeeded && (r.Id == a || r.Id == b))
            .ToListAsync(ct);
        if (a == b) return Results.BadRequest(new { error = "Выберите два разных прогона" });
        if (runs.Count != 2) return Results.NotFound(new { error = "Прогон не найден" });
        var ordered = runs.OrderBy(r => r.FinishedAt ?? r.CreatedAt).ToList();
        var (first, second) = (ordered[0], ordered[1]);
        var (from, to) = (first.FinishedAt ?? first.CreatedAt, second.FinishedAt ?? second.CreatedAt);

        var marked = await db.Set<ClashRunPairStat>().AsNoTracking()
            .Where(s => (s.RunId == a || s.RunId == b) && s.DisciplinePair == ClashRunPairStat.Marker).Select(s => s.RunId).ToListAsync(ct);
        if (ordered.FirstOrDefault(r => !marked.Contains(r.Id)) is { } old)
        {
            var zone = (await general.GetAsync(ct)).TimeZone;
            var at = TimeZoneInfo.ConvertTime(old.FinishedAt ?? old.CreatedAt, zone).ToString("dd.MM.yyyy HH:mm");
            return Results.Json(new { error = $"Прогон {at} был до обновления — сравнение недоступно" }, statusCode: StatusCodes.Status409Conflict);
        }

        var between = await db.Set<EngineRun>().AsNoTracking()
            .Where(r => r.PartId == id && r.Kind == EngineRunKind.Approved && r.State == EngineRunState.Succeeded && r.FinishedAt > from && r.FinishedAt <= to)
            .Select(r => r.Id).ToListAsync(ct);
        var runIds = between.Append(first.Id).Distinct().ToList();
        var stats = await db.Set<ClashRunPairStat>().AsNoTracking()
            .Where(s => runIds.Contains(s.RunId) && s.DisciplinePair != ClashRunPairStat.Marker).ToListAsync(ct);

        var rows = stats.Select(s => s.DisciplinePair).Distinct().Order(StringComparer.Ordinal).Select(pair =>
        {
            var moved = stats.Where(s => s.DisciplinePair == pair && between.Contains(s.RunId)).ToList();
            int Found(Guid run) => stats.FirstOrDefault(s => s.RunId == run && s.DisciplinePair == pair)?.Found ?? 0;
            return new RunCompareRow(pair, Found(first.Id), Found(second.Id), moved.Sum(s => s.New), moved.Sum(s => s.Returned), moved.Sum(s => s.Fixed),
                moved.Sum(s => s.GoneByRules));
        }).ToList();
        return Results.Ok(new RunCompare(first.Id, second.Id, rows));
    }
}
