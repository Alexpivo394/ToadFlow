using Microsoft.EntityFrameworkCore;
using ToadFlow.Server.Clash.Engine;
using ToadFlow.Server.Clash.Parts;
using ToadFlow.Server.Data;

namespace ToadFlow.Server.Clash.Plugin;

/// <summary>
///     Какая модель ToadFlow открыта в Revit: сначала по пути центральной модели (ключ, как у выгрузки), потом — по имени файла
///     без расширения и без учёта регистра. Хвост локальной копии <c>_{пользователь}</c> отбрасывает плагин.
/// </summary>
public sealed class ModelResolver(ToadFlowDb db, PartModels partModels)
{
    public async Task<ModelResolveView> FindAsync(string? path, string? name, CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(path))
        {
            var key = ClashStorage.KeyOf(path, "");
            var byPath = await db.Set<ClashModel>().AsNoTracking().Where(m => m.Key == key).ToListAsync(ct);
            if (byPath.Count > 0) return new ModelResolveView("path", await MatchesAsync(byPath, ct));
        }

        var file = FileOf(string.IsNullOrWhiteSpace(name) ? path : name);
        if (file.Length > 0)
        {
            // Моделей — сотни: имя файла из пути удобнее сравнить в памяти, чем выражать в SQL.
            var all = await db.Set<ClashModel>().AsNoTracking().ToListAsync(ct);
            var byName = all.Where(m => string.Equals(FileOf(m.Name), file, StringComparison.OrdinalIgnoreCase)).ToList();
            if (byName.Count > 0) return new ModelResolveView("name", await MatchesAsync(byName, ct));
        }

        return new ModelResolveView("none", []);
    }

    private static string FileOf(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "";
        var trimmed = value.Trim();
        var file = trimmed[(trimmed.LastIndexOfAny(['/', '\\']) + 1)..];
        return Path.GetFileNameWithoutExtension(file);
    }

    private async Task<IReadOnlyList<ModelMatchView>> MatchesAsync(IReadOnlyList<ClashModel> models, CancellationToken ct)
    {
        var ids = models.Select(m => m.Id).ToList();
        var links = await db.Set<ClashPartModel>().AsNoTracking().Where(l => ids.Contains(l.ModelId))
            .Join(db.Set<ClashPart>().AsNoTracking().Where(p => p.ApprovedVersion != null), l => l.PartId, p => p.Id,
                (l, p) => new { l.ModelId, p.Id, p.Name, p.ApprovedRunId })
            .ToListAsync(ct);
        var runIds = links.Select(l => l.ApprovedRunId).OfType<Guid>().Distinct().ToList();
        var finished = await db.Set<EngineRun>().AsNoTracking().Where(r => runIds.Contains(r.Id))
            .ToDictionaryAsync(r => r.Id, r => r.FinishedAt, ct);

        var result = new List<ModelMatchView>();
        foreach (var model in models.OrderBy(m => m.ModelPath, StringComparer.OrdinalIgnoreCase))
        {
            var parts = links.Where(l => l.ModelId == model.Id).OrderBy(l => l.Name, StringComparer.CurrentCultureIgnoreCase)
                .Select(l => new ModelPartView(l.Id, l.Name, l.ApprovedRunId is { } run ? finished.GetValueOrDefault(run) : null)).ToList();
            string? discipline = null;
            if (parts.Count > 0)
                discipline = (await partModels.OfPartAsync(parts[0].Id, ct)).FirstOrDefault(m => m.ModelId == model.Id)?.Discipline;
            result.Add(new ModelMatchView(model.SnapshotKey, model.Id, model.Name, model.ModelPath, discipline, parts));
        }

        return result;
    }
}
