using Microsoft.EntityFrameworkCore;
using ToadFlow.Server.Clash.Collisions;
using ToadFlow.Server.Clash.Engine;
using ToadFlow.Server.Data;

namespace ToadFlow.Server.Clash.Images;

internal static class ImageTargets
{
    /// <summary>
    ///     Коллизии и модели их прогонов (<see cref="ClashCollision.LastFoundRunId" />) двумя запросами. Нет коллизии или её прогона —
    ///     нет и в словаре.
    /// </summary>
    public static async Task<Dictionary<Guid, ImageTarget>> LoadAsync(ToadFlowDb db, IReadOnlyCollection<Guid> collisionIds, CancellationToken ct)
    {
        if (collisionIds.Count == 0) return [];
        var collisions = await db.Set<ClashCollision>().AsNoTracking().Where(c => collisionIds.Contains(c.Id))
            .Select(c => new { c.Id, c.PartId, c.LastFoundRunId, c.ModelA, c.UniqueIdA, c.ModelB, c.UniqueIdB, c.X, c.Y, c.Z })
            .ToListAsync(ct);
        var runIds = collisions.Select(c => c.LastFoundRunId).Distinct().ToList();
        var models = await db.Set<EngineRun>().AsNoTracking().Where(r => runIds.Contains(r.Id)).Select(r => new { r.Id, r.Models }).ToDictionaryAsync(r => r.Id, r => r.Models, ct);

        var targets = new Dictionary<Guid, ImageTarget>(collisions.Count);
        foreach (var c in collisions)
        {
            if (!models.TryGetValue(c.LastFoundRunId, out var runModels)) continue;
            targets[c.Id] = new ImageTarget(c.Id, c.PartId, c.LastFoundRunId, c.ModelA, c.UniqueIdA, c.ModelB, c.UniqueIdB, c.X, c.Y, c.Z, runModels);
        }

        return targets;
    }
}
