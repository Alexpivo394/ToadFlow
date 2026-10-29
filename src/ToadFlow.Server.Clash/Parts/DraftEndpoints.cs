using System.Text.Json;
using Alexpivo394.Revit.Clash.Rules;
using Microsoft.EntityFrameworkCore;
using ToadFlow.Server.Auth;
using ToadFlow.Server.Data;
using ToadFlow.Server.Events;

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
        return new DraftView(document.RootElement.Clone(), part.DraftVersion, disciplines, refs,
            ConfigurationValidator.Problems(config, disciplines, refs.Select(m => m.ModelKey).ToList()));
    }

    private static async Task<IResult> GetAsync(Guid id, ToadFlowDb db, PartModels models, CancellationToken ct)
    {
        var part = await db.Set<ClashPart>().AsNoTracking().SingleOrDefaultAsync(p => p.Id == id, ct);
        return part is null ? Results.NotFound() : Results.Ok(await ViewAsync(part, models, ct));
    }

    /// <summary>
    ///     Тело — <c>{ baseVersion, configuration, keepMine? }</c>: правки сливаются с тем, что сохранили другие после
    ///     <c>baseVersion</c> (<see cref="ConfigurationMerge" />); один и тот же элемент, изменённый по-разному, — 409 со списком
    ///     конфликтов и нынешним черновиком. Прежнее тело — сама конфигурация — сохраняется поверх.
    /// </summary>
    private static async Task<IResult> SaveAsync(Guid id, HttpRequest request, ToadFlowDb db, PartModels models, TimeProvider time, IDomainEvents events,
        CancellationToken ct)
    {
        using var reader = new StreamReader(request.Body);
        var text = await reader.ReadToEndAsync(ct);
        if (ReadBody(text) is not { } body) return Results.BadRequest(new { error = "Конфигурация не читается" });

        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        // Блокировка строки части: два одновременных сохранения сливаются по очереди, а не затирают друг друга.
        var part = await db.Set<ClashPart>().FromSqlInterpolated($"SELECT * FROM clash_parts WHERE id = {id} FOR UPDATE").SingleOrDefaultAsync(ct);
        if (part is null) return Results.NotFound();

        var config = body.Mine;
        if (body.BaseVersion is { } baseVersion && baseVersion != part.DraftVersion)
        {
            var current = ConfigurationJson.Deserialize(part.DraftJson);
            var baseJson = await db.Set<ClashDraftVersion>().Where(v => v.PartId == id && v.Version == baseVersion)
                .Select(v => v.ConfigurationJson).SingleOrDefaultAsync(ct);
            // Базы уже нет: всё, что у пользователя отличается от сохранённого, — конфликт.
            var @base = baseJson is null ? new ClashConfiguration([], [], [], [], current.IgnoreConnectedMep) : ConfigurationJson.Deserialize(baseJson);
            var merge = ConfigurationMerge.Merge(@base, current, config, body.KeepMine);
            if (merge.Conflicts.Count > 0)
                return Results.Conflict(new { error = "Эти элементы уже изменил другой пользователь", conflicts = merge.Conflicts, current = await ViewAsync(part, models, ct) });
            config = merge.Merged;
        }

        var problems = await ProblemsAsync(id, config, models, ct);
        if (problems.Count > 0) return Results.BadRequest(new { error = string.Join("; ", problems), problems });

        await StoreAsync(db, part, config, request.HttpContext, time, ct);
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        events.Publish(new Engine.ClashDraftChanged(request.HttpContext.User.Actor(), id));
        return Results.Ok(await ViewAsync(part, models, ct));
    }

    private sealed record SaveBody(ClashConfiguration Mine, int? BaseVersion, IReadOnlyCollection<string>? KeepMine);

    private static SaveBody? ReadBody(string text)
    {
        try
        {
            using var document = JsonDocument.Parse(text);
            var root = document.RootElement;
            if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("configuration", out var configuration))
            {
                if (TryParse(configuration.GetRawText()) is not { } mine) return null;
                int? baseVersion = root.TryGetProperty("baseVersion", out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : null;
                var keep = root.TryGetProperty("keepMine", out var k) && k.ValueKind == JsonValueKind.Array
                    ? k.EnumerateArray().Select(e => e.GetString()).OfType<string>().ToList()
                    : null;
                return new SaveBody(mine, baseVersion, keep);
            }
        }
        catch (JsonException)
        {
            return null;
        }

        return TryParse(text) is { } legacy ? new SaveBody(legacy, null, null) : null;
    }

    /// <summary>Добавляет клетки для всех пар групп черновика (и группы с самой собой): включены, 20 мм. Заданные клетки не меняются.</summary>
    private static async Task<IResult> FillMatrixAsync(Guid id, HttpContext http, ToadFlowDb db, PartModels models, TimeProvider time, IDomainEvents events,
        CancellationToken ct)
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

        await StoreAsync(db, part, config with { Matrix = matrix }, http, time, ct);
        await db.SaveChangesAsync(ct);
        events.Publish(new Engine.ClashDraftChanged(http.User.Actor(), id));
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

    /// <summary>
    ///     Новая версия черновика: версия растёт, сама версия запоминается как база для слияния правок (последние
    ///     <see cref="ClashDraftVersion.Keep" />). Изменения сохраняет вызывающий.
    /// </summary>
    public static async Task StoreAsync(ToadFlowDb db, ClashPart part, ClashConfiguration config, HttpContext http, TimeProvider time, CancellationToken ct)
    {
        var now = time.GetUtcNow();
        var user = http.User.UserId();
        // Черновик, сохранённый до появления версий или только что созданный, тоже должен быть базой для слияния.
        if (!await db.Set<ClashDraftVersion>().AnyAsync(v => v.PartId == part.Id && v.Version == part.DraftVersion, ct))
            db.Set<ClashDraftVersion>().Add(new ClashDraftVersion
                { PartId = part.Id, Version = part.DraftVersion, ConfigurationJson = part.DraftJson, UserId = part.UpdatedByUserId, SavedAt = part.UpdatedAt });

        part.DraftVersion++;
        part.DraftJson = ConfigurationJson.Serialize(config);
        part.UpdatedAt = now;
        part.UpdatedByUserId = user;
        db.Set<ClashDraftVersion>().Add(new ClashDraftVersion
            { PartId = part.Id, Version = part.DraftVersion, ConfigurationJson = part.DraftJson, UserId = user, SavedAt = now });
        var oldest = part.DraftVersion - ClashDraftVersion.Keep;
        await db.Set<ClashDraftVersion>().Where(v => v.PartId == part.Id && v.Version <= oldest).ExecuteDeleteAsync(ct);
    }
}
