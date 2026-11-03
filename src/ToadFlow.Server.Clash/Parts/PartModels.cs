using Microsoft.EntityFrameworkCore;
using ToadFlow.Server.Data;
using ToadFlow.Server.Naming;

namespace ToadFlow.Server.Clash.Parts;

/// <summary>Модель для коллизий с разбором её имени и последним слепком.</summary>
public sealed record ClassifiedModel(ClashModel Model, ModelClassification Classification, ClashSnapshotView? Latest)
{
    public PartModelView View(string? disciplineOverride) => new(Model.Id, Model.Name, Model.ModelPath, Classification.Discipline, disciplineOverride,
        disciplineOverride ?? Classification.Discipline, Latest);
}

/// <summary>Модели частей: разделы по справочнику разбора имён и последние слепки.</summary>
public sealed class PartModels(ToadFlowDb db, NamingStore naming)
{
    public async Task<IReadOnlyList<ClassifiedModel>> AllAsync(CancellationToken ct) => await ClassifyAsync(db.Set<ClashModel>().AsNoTracking(), ct);

    /// <summary>Модели части по имени; у каждой — поправка раздела, если задана.</summary>
    public async Task<IReadOnlyList<PartModelView>> OfPartAsync(Guid partId, CancellationToken ct)
    {
        var links = await db.Set<ClashPartModel>().AsNoTracking().Where(m => m.PartId == partId).ToDictionaryAsync(m => m.ModelId, ct);
        var ids = links.Keys.ToList();
        var models = await ClassifyAsync(db.Set<ClashModel>().AsNoTracking().Where(m => ids.Contains(m.Id)), ct);
        return models.Select(m => m.View(links[m.Model.Id].DisciplineOverride)).OrderBy(m => m.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>Отличается ли нынешний состав части от утверждённого: правка войдёт в проверку только после утверждения.</summary>
    public async Task<bool> CompositionPendingAsync(ClashPart part, IReadOnlyList<PartModelView> current, CancellationToken ct)
    {
        if (part.ApprovedVersion is not { } version) return false;
        var json = await db.Set<ClashPartVersion>().Where(v => v.PartId == part.Id && v.Version == version).Select(v => v.ModelsJson).SingleOrDefaultAsync(ct);
        return VersionModels.Parse(json) is { } frozen && !VersionModels.Same(frozen, VersionModels.Of(current));
    }

    /// <summary>
    ///     Модели утверждённой версии: состав и разделы — из снимка версии, слепки — последние. Модель, удалённая после утверждения,
    ///     остаётся без слепка (прогон её не возьмёт и скажет почему). Без снимка — нынешний состав части.
    /// </summary>
    public async Task<IReadOnlyList<PartModelView>> OfVersionAsync(Guid partId, IReadOnlyList<VersionModel>? frozen, CancellationToken ct)
    {
        if (frozen is null) return await OfPartAsync(partId, ct);
        var ids = frozen.Select(m => m.ModelId).ToList();
        var found = (await ClassifyAsync(db.Set<ClashModel>().AsNoTracking().Where(m => ids.Contains(m.Id)), ct)).ToDictionary(m => m.Model.Id);
        return frozen.Select(m => found.TryGetValue(m.ModelId, out var model)
                ? new PartModelView(m.ModelId, model.Model.Name, model.Model.ModelPath, model.Classification.Discipline, null, m.Discipline, model.Latest)
                : new PartModelView(m.ModelId, m.ModelName, "", null, null, m.Discipline, null))
            .OrderBy(m => m.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private async Task<IReadOnlyList<ClassifiedModel>> ClassifyAsync(IQueryable<ClashModel> query, CancellationToken ct)
    {
        var models = await query.ToListAsync(ct);
        var ids = models.Select(m => m.Id).ToList();
        // Одинаковое время выгрузки бывает у повторной загрузки того же слепка: последней считается загруженная позже.
        var latest = (await db.Set<ClashSnapshot>().AsNoTracking().Where(s => ids.Contains(s.ModelId))
                .OrderByDescending(s => s.ExtractedAt).ThenByDescending(s => s.CreatedAt)
                .Select(s => new { s.ModelId, View = new ClashSnapshotView(s.Id, s.ExtractedAt, s.RevitYear, s.ViewName, s.Elements, s.Triangles, s.Inventory,
                    s.GeometryBytes, s.ParametersBytes, s.JobId) })
                .ToListAsync(ct))
            .GroupBy(s => s.ModelId)
            .ToDictionary(g => g.Key, g => g.First().View);
        var parser = new ModelNameParser(await naming.GetAsync(ct));
        return models.Select(m => new ClassifiedModel(m, parser.Parse(m.Name), latest.GetValueOrDefault(m.Id))).ToList();
    }
}
