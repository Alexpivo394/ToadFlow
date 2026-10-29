using Microsoft.EntityFrameworkCore;
using ToadFlow.Server.Clash.Parts;
using ToadFlow.Server.Data;

namespace ToadFlow.Server.Clash.Projects;

/// <summary>Проект — объект из разбора имён моделей (и части с этим объектом).</summary>
/// <param name="Object">Объект; null — модели и части, у которых объект не определён.</param>
public sealed record ClashProjectView(string? Object, string Name, int Parts, int Models);

/// <param name="Stale">Выгрузки нет или она старше <see cref="ProjectEndpoints.StaleAfter" />.</param>
public sealed record ProjectModelView(Guid Id, string Name, string? Part, string? Discipline, ClashSnapshotView? Latest, int SnapshotCount, bool Stale);

/// <summary>Проекты для главной страницы коллизий: части и выгрузки сгруппированы по объекту.</summary>
public static class ProjectEndpoints
{
    public const string Unparsed = "Без проекта";

    /// <summary>Выгрузка старше этого — устаревшая: проверять по ней можно, но стоит выгрузить заново.</summary>
    public static readonly TimeSpan StaleAfter = TimeSpan.FromDays(7);

    public static void MapClashProjects(this RouteGroupBuilder api)
    {
        var projects = api.MapGroup("/clash/projects");
        projects.MapGet("", ListAsync);
        projects.MapGet("/models", ModelsAsync);
    }

    /// <summary>Устарела ли выгрузка модели: её нет или она старше недели.</summary>
    public static bool IsStale(ClashSnapshotView? latest, DateTimeOffset now) => latest is null || now - latest.ExtractedAt > StaleAfter;

    private static async Task<ClashProjectView[]> ListAsync(ToadFlowDb db, PartModels models, CancellationToken ct)
    {
        var modelCounts = (await models.AllAsync(ct)).GroupBy(m => Normalize(m.Classification.Object)).ToDictionary(g => g.Key ?? "", g => g.Count());
        var partCounts = (await db.Set<ClashPart>().AsNoTracking().Select(p => p.Object).ToListAsync(ct))
            .GroupBy(Normalize).ToDictionary(g => g.Key ?? "", g => g.Count());
        var named = modelCounts.Keys.Concat(partCounts.Keys).Where(k => k != "").Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.CurrentCultureIgnoreCase)
            .Select(o => new ClashProjectView(o, o, Count(partCounts, o), Count(modelCounts, o)));
        var unparsed = partCounts.ContainsKey("") || modelCounts.ContainsKey("")
            ? [new ClashProjectView(null, Unparsed, partCounts.GetValueOrDefault(""), modelCounts.GetValueOrDefault(""))]
            : Array.Empty<ClashProjectView>();
        return [.. named, .. unparsed];
    }

    private static async Task<ProjectModelView[]> ModelsAsync(string? @object, ToadFlowDb db, PartModels models, TimeProvider time, CancellationToken ct)
    {
        var wanted = Normalize(@object);
        var all = (await models.AllAsync(ct)).Where(m => string.Equals(Normalize(m.Classification.Object), wanted, StringComparison.OrdinalIgnoreCase)).ToList();
        var ids = all.Select(m => m.Model.Id).ToList();
        var counts = await db.Set<ClashSnapshot>().AsNoTracking().Where(s => ids.Contains(s.ModelId)).GroupBy(s => s.ModelId)
            .Select(g => new { g.Key, Count = g.Count() }).ToDictionaryAsync(g => g.Key, g => g.Count, ct);
        var now = time.GetUtcNow();
        return all.OrderBy(m => m.Model.Name, StringComparer.OrdinalIgnoreCase)
            .Select(m => new ProjectModelView(m.Model.Id, m.Model.Name, m.Classification.Part, m.Classification.Discipline, m.Latest,
                counts.GetValueOrDefault(m.Model.Id), IsStale(m.Latest, now)))
            .ToArray();
    }

    private static int Count(Dictionary<string, int> counts, string key) =>
        counts.Where(c => string.Equals(c.Key, key, StringComparison.OrdinalIgnoreCase)).Sum(c => c.Value);

    private static string? Normalize(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
