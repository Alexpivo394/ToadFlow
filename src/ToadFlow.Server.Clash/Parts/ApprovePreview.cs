using Alexpivo394.Revit.Clash.Rules;
using Microsoft.EntityFrameworkCore;
using ToadFlow.Server.Clash.Collisions;
using ToadFlow.Server.Clash.Engine;
using ToadFlow.Server.Data;

namespace ToadFlow.Server.Clash.Parts;

/// <param name="Gone">Открытые коллизии части, которых нет в прогоне черновика: уйдут из-за правил.</param>
/// <param name="New">Находки черновика, которых нет среди коллизий части (любого статуса): появятся.</param>
/// <param name="Same">Находки черновика среди открытых коллизий: без изменений.</param>
public sealed record ApprovePreviewPair(string Pair, int Gone, int New, int Same);

/// <summary>Что будет, если утвердить черновик, — приблизительно, по последнему прогону черновика.</summary>
/// <param name="DraftRunAt">Когда закончен прогон черновика, по которому посчитано; null — прогона нет.</param>
/// <param name="Stale">Черновик изменён после прогона: числа устарели.</param>
/// <param name="NeverRun">Черновик ещё не прогонялся: чисел нет.</param>
/// <param name="FirstApproval">Часть ещё не утверждалась: коллизий нет, все находки — новые.</param>
/// <param name="Changes">Отличия черновика от утверждённой версии.</param>
public sealed record ApprovePreview(
    DateTimeOffset? DraftRunAt,
    bool Stale,
    bool NeverRun,
    bool FirstApproval,
    IReadOnlyList<ApprovePreviewPair> Pairs,
    IReadOnlyList<ConfigChange> Changes);

/// <summary>Предпросмотр утверждения и «Что изменилось» в версии.</summary>
public static class ApprovePreviews
{
    /// <summary>
    ///     Пары элементов сравниваются по ключу (модель A, UniqueId A, модель B, UniqueId B): открытые коллизии части против находок
    ///     последнего удачного прогона черновика. Считает база — запросы NOT EXISTS / EXISTS по ключу с группировкой по паре разделов.
    /// </summary>
    public static async Task<IResult> PreviewAsync(Guid id, ToadFlowDb db, CancellationToken ct)
    {
        var part = await db.Set<ClashPart>().AsNoTracking().SingleOrDefaultAsync(p => p.Id == id, ct);
        if (part is null) return Results.NotFound();
        var draft = ConfigurationJson.Deserialize(part.DraftJson);
        var changes = part.ApprovedVersion is { } approved
            ? ConfigurationDiff.Compare(ConfigurationJson.Deserialize(await VersionJsonAsync(db, id, approved, ct) ?? part.DraftJson), draft)
            : [];
        var run = part.ResultRunId is { } runId ? await db.Set<EngineRun>().AsNoTracking().SingleOrDefaultAsync(r => r.Id == runId, ct) : null;
        if (run is null) return Results.Ok(new ApprovePreview(null, false, true, part.ApprovedVersion is null, [], changes));

        var hits = db.Set<ClashDraftHit>().AsNoTracking().Where(h => h.RunId == run.Id);
        var all = db.Set<ClashCollision>().AsNoTracking().Where(c => c.PartId == id);
        var open = all.Where(c => CollisionEndpoints.Open.Contains(c.Status));

        var gone = await open
            .Where(c => !hits.Any(h => h.ModelA == c.ModelA && h.UniqueIdA == c.UniqueIdA && h.ModelB == c.ModelB && h.UniqueIdB == c.UniqueIdB))
            .GroupBy(c => c.DisciplinePair).Select(g => new { g.Key, Count = g.Count() }).ToListAsync(ct);
        var fresh = await hits
            .Where(h => !all.Any(c => h.ModelA == c.ModelA && h.UniqueIdA == c.UniqueIdA && h.ModelB == c.ModelB && h.UniqueIdB == c.UniqueIdB))
            .GroupBy(h => h.DisciplinePair).Select(g => new { g.Key, Count = g.Count() }).ToListAsync(ct);
        var same = await hits
            .Where(h => open.Any(c => h.ModelA == c.ModelA && h.UniqueIdA == c.UniqueIdA && h.ModelB == c.ModelB && h.UniqueIdB == c.UniqueIdB))
            .GroupBy(h => h.DisciplinePair).Select(g => new { g.Key, Count = g.Count() }).ToListAsync(ct);

        int Of<T>(IEnumerable<T> rows, Func<T, string> key, Func<T, int> count, string pair) => rows.Where(r => key(r) == pair).Sum(count);
        var pairs = gone.Select(g => g.Key).Concat(fresh.Select(f => f.Key)).Concat(same.Select(s => s.Key)).Distinct().Order(StringComparer.Ordinal)
            .Select(p => new ApprovePreviewPair(p, Of(gone, g => g.Key, g => g.Count, p), Of(fresh, f => f.Key, f => f.Count, p), Of(same, s => s.Key, s => s.Count, p)))
            .ToList();
        return Results.Ok(new ApprovePreview(run.FinishedAt ?? run.CreatedAt, !ApproveEndpoints.SameConfiguration(run.ConfigurationJson, part.DraftJson), false,
            part.ApprovedVersion is null, pairs, changes));
    }

    /// <summary>Отличия версии от предыдущей; у первой — пусто.</summary>
    public static async Task<IResult> ChangesAsync(Guid id, int version, ToadFlowDb db, CancellationToken ct)
    {
        if (await VersionJsonAsync(db, id, version, ct) is not { } json) return Results.NotFound();
        return await VersionJsonAsync(db, id, version - 1, ct) is { } previous
            ? Results.Ok(ConfigurationDiff.Compare(ConfigurationJson.Deserialize(previous), ConfigurationJson.Deserialize(json)))
            : Results.Ok(Array.Empty<ConfigChange>());
    }

    private static Task<string?> VersionJsonAsync(ToadFlowDb db, Guid id, int version, CancellationToken ct) =>
        db.Set<ClashPartVersion>().AsNoTracking().Where(v => v.PartId == id && v.Version == version).Select(v => v.ConfigurationJson).SingleOrDefaultAsync(ct);
}
