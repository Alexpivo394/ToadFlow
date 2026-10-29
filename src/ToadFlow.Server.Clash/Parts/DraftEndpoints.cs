using System.Text.Json;
using Alexpivo394.Revit.Clash.Rules;
using Microsoft.EntityFrameworkCore;
using ToadFlow.Server.Auth;
using ToadFlow.Server.Data;

namespace ToadFlow.Server.Clash.Parts;

/// <summary>Модель части для редактора: ключ (Id модели в форме N, как в выгрузке), имя и раздел.</summary>
public sealed record PartModelRef(string ModelKey, string Name, string? Discipline);

/// <param name="Configuration">Черновик в формате <see cref="ConfigurationJson" /> с группами всех разделов части.</param>
/// <param name="Version">Версия черновика: растёт на каждом сохранении.</param>
/// <param name="Disciplines">Разделы моделей части по алфавиту.</param>
/// <param name="Models">Модели части — источники своих групп.</param>
/// <param name="Problems">Ошибки черновика при нынешнем составе моделей: модели могли смениться после сохранения.</param>
public sealed record DraftView(JsonElement Configuration, int Version, IReadOnlyList<string> Disciplines, IReadOnlyList<PartModelRef> Models,
    IReadOnlyList<string> Problems);

/// <summary>Черновик конфигурации части: группы, наборы, матрица, исключения. Читает Наблюдатель, меняет Оператор.</summary>
public static class DraftEndpoints
{
    public const double DefaultToleranceMm = 20;

    public static void MapClashDrafts(this RouteGroupBuilder api)
    {
        var draft = api.MapGroup("/clash/parts/{id:guid}/draft");
        draft.MapGet("", GetAsync);

        var write = draft.MapGroup("").RequireAuthorization(Policies.Operator);
        write.MapPut("", SaveAsync);
        write.MapPost("/matrix/fill", FillMatrixAsync);
    }

    /// <summary>Разбирает конфигурацию из JSON; null — JSON неверен или в нём нет списков.</summary>
    public static ClashConfiguration? TryParse(string json)
    {
        try
        {
            return Valid(ConfigurationJson.Deserialize(json));
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Конфигурация без null-списков и null-элементов; иначе null — её не разобрать дальше.</summary>
    public static ClashConfiguration? Valid(ClashConfiguration? config)
    {
        if (config?.Groups is null || config.Sets is null || config.Matrix is null || config.Exclusions is null) return null;
        if (config.Groups.Any(g => g?.Name is null || g.Source?.Disciplines is null || g.Source.ModelKeys is null
                                   || g.Source.Disciplines.Any(d => d is null) || g.Source.ModelKeys.Any(k => k is null)))
            return null;
        if (config.Sets.Any(s => s?.AnyOf is null || s.Name is null || s.AnyOf.Any(g => g?.All is null || g.All.Any(c => c?.Property is null))))
            return null;
        return config.Matrix.Any(c => c is null) || config.Exclusions.Any(e => e is null) ? null : config;
    }

    /// <summary>Разделы моделей части по алфавиту; модели без раздела не в счёт.</summary>
    public static async Task<IReadOnlyList<string>> DisciplinesAsync(Guid partId, PartModels models, CancellationToken ct) =>
        Disciplines(await models.OfPartAsync(partId, ct));

    private static IReadOnlyList<string> Disciplines(IReadOnlyList<PartModelView> models) =>
        models.Select(m => m.Discipline).OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToList();

    /// <summary>Разделы и ключи моделей части — против них проверяются источники групп.</summary>
    public static async Task<(IReadOnlyList<string> Disciplines, IReadOnlyList<PartModelRef> Models)> ContextAsync(Guid partId, PartModels models,
        CancellationToken ct)
    {
        var all = await models.OfPartAsync(partId, ct);
        return (Disciplines(all), all.Select(m => new PartModelRef(m.ModelId.ToString("N"), m.Name, m.Discipline)).ToList());
    }

    /// <summary>Ошибки конфигурации против нынешнего состава моделей части.</summary>
    public static async Task<IReadOnlyList<string>> ProblemsAsync(Guid partId, ClashConfiguration config, PartModels models, CancellationToken ct)
    {
        var (disciplines, refs) = await ContextAsync(partId, models, ct);
        return ConfigurationValidator.Problems(config, disciplines, refs.Select(m => m.ModelKey).ToList());
    }

    /// <summary>Группы разделов создаются сами: у каждого раздела моделей части есть своя группа, даже без наборов.</summary>
    public static ClashConfiguration WithDisciplineGroups(ClashConfiguration config, IReadOnlyList<string> disciplines)
    {
        var known = config.Groups.Select(g => g.Id).ToHashSet();
        var missing = disciplines.Select(ConfigurationDefaults.DisciplineGroup).Where(g => known.Add(g.Id)).ToList();
        return missing.Count == 0 ? config : config with { Groups = [.. config.Groups, .. missing] };
    }

    /// <summary>Группа раздела, созданная автоматически: источник — один раздел, Id выведен из него.</summary>
    public static bool IsDisciplineGroup(SetGroup group) =>
        group.Source is { Disciplines.Count: 1, ModelKeys.Count: 0 } && group.Id == ConfigurationDefaults.DisciplineGroupId(group.Source.Disciplines[0]);

    public static async Task<DraftView> ViewAsync(ClashPart part, PartModels models, CancellationToken ct)
    {
        var (disciplines, refs) = await ContextAsync(part.Id, models, ct);
        var config = WithDisciplineGroups(ConfigurationJson.Deserialize(part.DraftJson), disciplines);
        using var document = JsonDocument.Parse(ConfigurationJson.Serialize(config));
        return new DraftView(document.RootElement.Clone(), 0, disciplines, refs,
            ConfigurationValidator.Problems(config, disciplines, refs.Select(m => m.ModelKey).ToList()));
    }

    private static async Task<IResult> GetAsync(Guid id, ToadFlowDb db, PartModels models, CancellationToken ct)
    {
        var part = await db.Set<ClashPart>().AsNoTracking().SingleOrDefaultAsync(p => p.Id == id, ct);
        return part is null ? Results.NotFound() : Results.Ok(await ViewAsync(part, models, ct));
    }

    private static async Task<IResult> SaveAsync(Guid id, HttpRequest request, ToadFlowDb db, PartModels models, TimeProvider time, CancellationToken ct)
    {
        var part = await db.Set<ClashPart>().SingleOrDefaultAsync(p => p.Id == id, ct);
        if (part is null) return Results.NotFound();

        using var reader = new StreamReader(request.Body);
        if (TryParse(await reader.ReadToEndAsync(ct)) is not { } config) return Results.BadRequest(new { error = "Конфигурация не читается" });

        var problems = await ProblemsAsync(id, config, models, ct);
        if (problems.Count > 0) return Results.BadRequest(new { error = string.Join("; ", problems), problems });

        Store(part, config, request.HttpContext, time);
        await db.SaveChangesAsync(ct);
        return Results.Ok(await ViewAsync(part, models, ct));
    }

    /// <summary>Добавляет клетки для всех пар групп черновика (и группы с самой собой): включены, 20 мм. Заданные клетки не меняются.</summary>
    private static async Task<IResult> FillMatrixAsync(Guid id, HttpContext http, ToadFlowDb db, PartModels models, TimeProvider time, CancellationToken ct)
    {
        var part = await db.Set<ClashPart>().SingleOrDefaultAsync(p => p.Id == id, ct);
        if (part is null) return Results.NotFound();

        var config = WithDisciplineGroups(ConfigurationJson.Deserialize(part.DraftJson), await DisciplinesAsync(id, models, ct));
        var groups = config.Groups;
        var matrix = config.Matrix.ToList();
        for (var i = 0; i < groups.Count; i++)
        for (var j = i; j < groups.Count; j++)
            if (config.CellFor(groups[i].Id, groups[j].Id) is null)
                matrix.Add(new MatrixCell(groups[i].Id, groups[j].Id, true, DefaultToleranceMm));

        Store(part, config with { Matrix = matrix }, http, time);
        await db.SaveChangesAsync(ct);
        return Results.Ok(await ViewAsync(part, models, ct));
    }

    /// <summary>
    ///     Копия чужой конфигурации (шаблона или другой части) для этой части: наборы получают новые Id, исключения переводятся
    ///     на них, у своих групп остаются только модели этой части. Группы разделов, которых в части нет, и их клетки
    ///     отбрасываются, если у группы нет наборов; наборы чужих разделов остаются — их покажут проблемы черновика.
    /// </summary>
    public static ClashConfiguration CopyFor(ClashConfiguration source, IReadOnlyList<string> disciplines, IReadOnlyCollection<string> modelKeys)
    {
        var present = new HashSet<string>(disciplines, StringComparer.OrdinalIgnoreCase);
        var keys = new HashSet<string>(modelKeys, StringComparer.Ordinal);
        var withSets = source.Sets.Select(s => s.GroupId).ToHashSet();
        var groups = source.Groups
            .Where(g => !IsDisciplineGroup(g) || present.Contains(g.Source.Disciplines[0]) || withSets.Contains(g.Id))
            .Select(g => IsDisciplineGroup(g) ? g : g with { Source = g.Source with { ModelKeys = g.Source.ModelKeys.Where(keys.Contains).ToList() } })
            .ToList();
        var kept = groups.Select(g => g.Id).ToHashSet();
        var ids = source.Sets.ToDictionary(s => s.Id, _ => Guid.NewGuid());
        return source with
        {
            Groups = groups,
            Sets = source.Sets.Select(s => s with { Id = ids[s.Id] }).ToList(),
            Matrix = source.Matrix.Where(c => kept.Contains(c.GroupA) && kept.Contains(c.GroupB)).ToList(),
            Exclusions = source.Exclusions.Where(e => ids.ContainsKey(e.SetA) && ids.ContainsKey(e.SetB))
                .Select(e => new SetPairExclusion(ids[e.SetA], ids[e.SetB])).ToList()
        };
    }

    public static void Store(ClashPart part, ClashConfiguration config, HttpContext http, TimeProvider time)
    {
        part.DraftJson = ConfigurationJson.Serialize(config);
        part.UpdatedAt = time.GetUtcNow();
        part.UpdatedByUserId = http.User.UserId();
    }
}
