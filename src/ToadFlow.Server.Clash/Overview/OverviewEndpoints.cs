using Microsoft.EntityFrameworkCore;
using ToadFlow.Server.Auth;
using ToadFlow.Server.Clash.Collisions;
using ToadFlow.Server.Clash.Engine;
using ToadFlow.Server.Clash.Parts;
using ToadFlow.Server.Data;

namespace ToadFlow.Server.Clash.Overview;

/// <param name="Count">Число для показа: новых, коллизий, дней — смотря по виду проблемы.</param>
/// <param name="Reason">Короткий текст: «прогон упал: …», «+120 новых», «висят 340».</param>
public sealed record OverviewPart(Guid PartId, string Name, string? Object, int Count, string Reason);

public sealed record OverviewModel(Guid PartId, string PartName, Guid ModelId, string ModelName, string Reason);

/// <summary>Где проблема по всем утверждённым проверкам. Пусто во всех списках — «всё в порядке».</summary>
public sealed record OverviewView(
    IReadOnlyList<OverviewModel> NotUploaded,
    IReadOnlyList<OverviewPart> RunFailed,
    IReadOnlyList<OverviewPart> NoRecentRun,
    IReadOnlyList<OverviewPart> Unchecked,
    IReadOnlyList<OverviewPart> Spike,
    IReadOnlyList<OverviewPart> Stuck);

/// <summary>Обзор координатора: первым делом утром — что не выгрузилось, не прогналось, всплески и висящие коллизии.</summary>
public static class OverviewEndpoints
{
    public static void MapClashOverview(this RouteGroupBuilder api) =>
        api.MapGet("/clash/overview", OverviewAsync).RequireAuthorization(Policies.Operator);

    private static async Task<OverviewView> OverviewAsync(ToadFlowDb db, PartModels models, ClashSettingsStore settings, TimeProvider time,
        CancellationToken ct)
    {
        var limits = await settings.OverviewAsync(ct);
        var now = time.GetUtcNow();
        var parts = await db.Set<ClashPart>().AsNoTracking().Where(p => p.ApprovedVersion != null)
            .OrderBy(p => p.Name).Select(p => new { p.Id, p.Name, p.Object, p.ApprovedRunId }).ToListAsync(ct);
        var ids = parts.Select(p => p.Id).ToList();

        // Последний утверждённый прогон части — любой, в том числе упавший или ждущий.
        var latest = (await db.Set<EngineRun>().AsNoTracking().Where(r => ids.Contains(r.PartId) && r.Kind == EngineRunKind.Approved)
                .Select(r => new { r.PartId, r.State, r.Error, r.UncheckedModels, r.CreatedAt }).ToListAsync(ct))
            .GroupBy(r => r.PartId).ToDictionary(g => g.Key, g => g.MaxBy(r => r.CreatedAt)!);
        var succeededIds = parts.Select(p => p.ApprovedRunId).OfType<Guid>().ToList();
        var succeeded = await db.Set<EngineRun>().AsNoTracking().Where(r => succeededIds.Contains(r.Id))
            .Select(r => new { r.PartId, r.FinishedAt, r.Summary, r.PreviousRunId, r.UncheckedModels }).ToDictionaryAsync(r => r.PartId, ct);

        var notUploaded = new List<OverviewModel>();
        var classified = (await models.AllAsync(ct)).ToDictionary(m => m.Model.Id);
        var partModels = (await db.Set<ClashPartModel>().AsNoTracking().Where(m => ids.Contains(m.PartId)).ToListAsync(ct)).ToLookup(m => m.PartId);
        var staleAfter = TimeSpan.FromDays(limits.StaleDays);
        foreach (var part in parts)
        {
            var unchecked_ = succeeded.GetValueOrDefault(part.Id)?.UncheckedModels ?? [];
            foreach (var model in partModels[part.Id].Select(m => classified.GetValueOrDefault(m.ModelId)).OfType<ClassifiedModel>()
                         .OrderBy(m => m.Model.Name, StringComparer.OrdinalIgnoreCase))
            {
                var reason = model.Latest is null ? "нет выгрузки"
                    : unchecked_.Contains(model.Model.Id) ? "не выгрузилась в последнем цикле"
                    : now - model.Latest.ExtractedAt > staleAfter ? $"выгрузка старше {limits.StaleDays} дней"
                    : null;
                if (reason is not null) notUploaded.Add(new OverviewModel(part.Id, part.Name, model.Model.Id, model.Model.Name, reason));
            }
        }

        var runFailed = parts.Where(p => latest.GetValueOrDefault(p.Id)?.State is EngineRunState.Failed or EngineRunState.Cancelled)
            .Select(p => latest[p.Id] is var run && run.State == EngineRunState.Failed
                ? new OverviewPart(p.Id, p.Name, p.Object, 0, $"прогон упал: {run.Error ?? "без описания"}")
                : new OverviewPart(p.Id, p.Name, p.Object, 0, "прогон отменён"))
            .ToList();

        var noRunAfter = TimeSpan.FromDays(limits.NoRunDays);
        var noRecentRun = parts.Select(p => (Part: p, Finished: succeeded.GetValueOrDefault(p.Id)?.FinishedAt))
            .Where(p => p.Finished is null || now - p.Finished > noRunAfter)
            .Select(p => p.Finished is { } finished
                ? new OverviewPart(p.Part.Id, p.Part.Name, p.Part.Object, (int)(now - finished).TotalDays, $"не прогонялась {(int)(now - finished).TotalDays} дней")
                : new OverviewPart(p.Part.Id, p.Part.Name, p.Part.Object, 0, "ещё не прогонялась"))
            .ToList();

        var uncheckedCounts = await CountsAsync(db.Set<ClashCollision>().AsNoTracking().Where(c => ids.Contains(c.PartId) && c.Status == CollisionStatus.Unchecked), ct);
        var uncheckedParts = parts.Where(p => uncheckedCounts.ContainsKey(p.Id))
            .Select(p => new OverviewPart(p.Id, p.Name, p.Object, uncheckedCounts[p.Id], $"не проверены {uncheckedCounts[p.Id]}")).ToList();

        // Первый прогон после утверждения находит всё впервые — это не всплеск.
        var spike = parts.Select(p => (Part: p, Run: succeeded.GetValueOrDefault(p.Id)))
            .Where(p => p.Run is { Summary: { } s, PreviousRunId: not null } && s.New > limits.SpikeMin && s.New > limits.SpikeShare * s.Active)
            .Select(p => new OverviewPart(p.Part.Id, p.Part.Name, p.Part.Object, p.Run!.Summary!.New, $"+{p.Run.Summary.New} новых"))
            .ToList();

        var stuckCounts = await CountsAsync(Stuck(db, db.Set<ClashCollision>().AsNoTracking().Where(c => ids.Contains(c.PartId)), now - TimeSpan.FromDays(limits.StuckDays)), ct);
        var stuck = parts.Where(p => stuckCounts.ContainsKey(p.Id))
            .Select(p => new OverviewPart(p.Id, p.Name, p.Object, stuckCounts[p.Id], $"висят {stuckCounts[p.Id]}")).ToList();

        return new OverviewView(notUploaded, runFailed, noRecentRun, uncheckedParts, spike, stuck);
    }

    /// <summary>Открытые коллизии без движения: ни одной записи в ленте (и создания) после <paramref name="before" />.</summary>
    public static IQueryable<ClashCollision> Stuck(ToadFlowDb db, IQueryable<ClashCollision> query, DateTimeOffset before) =>
        query.Where(c => CollisionEndpoints.Open.Contains(c.Status) && c.CreatedAt < before
                         && !db.Set<ClashCollisionEvent>().Any(e => e.CollisionId == c.Id && e.At >= before));

    private static Task<Dictionary<Guid, int>> CountsAsync(IQueryable<ClashCollision> query, CancellationToken ct) =>
        query.GroupBy(c => c.PartId).Select(g => new { g.Key, Count = g.Count() }).ToDictionaryAsync(g => g.Key, g => g.Count, ct);
}
