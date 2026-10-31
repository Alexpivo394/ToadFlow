using Alexpivo394.Revit.Clash.Rules;
using Microsoft.EntityFrameworkCore;
using ToadFlow.Server.Auth;
using ToadFlow.Server.Data;
using ToadFlow.Server.Naming;

namespace ToadFlow.Server.Clash.Parts;

/// <summary>Части проекта: модели и их разделы. Читает Наблюдатель, меняет Оператор.</summary>
public static class PartEndpoints
{
    public const string Entity = "clash-parts";

    public static void MapClashParts(this RouteGroupBuilder api)
    {
        var parts = api.MapGroup("/clash/parts");
        parts.MapGet("", ListAsync);
        parts.MapGet("/suggest", SuggestAsync);
        parts.MapGet("/{id:guid}", GetAsync);

        var write = parts.MapGroup("").RequireAuthorization(Policies.Operator);
        write.MapPost("", CreateAsync);
        write.MapPut("/{id:guid}", UpdateAsync);
        write.MapDelete("/{id:guid}", DeleteAsync);
        write.MapPut("/{id:guid}/models", SetModelsAsync);
    }

    /// <param name="object">Проект: части с этим объектом; пустая строка — части без объекта; не задан — все.</param>
    private static async Task<ClashPartSummary[]> ListAsync(string? @object, ToadFlowDb db, PartModels models, TimeProvider time, CancellationToken ct)
    {
        var query = db.Set<ClashPart>().AsNoTracking();
        if (@object is not null)
            query = string.IsNullOrWhiteSpace(@object) ? query.Where(p => p.Object == null) : query.Where(p => p.Object!.ToLower() == @object.Trim().ToLower());
        var parts = await query.OrderBy(p => p.Name).ToListAsync(ct);
        var ids = parts.Select(p => p.Id).ToList();
        var runs = (await db.Set<Engine.EngineRun>().AsNoTracking().Where(r => ids.Contains(r.PartId) && r.Kind == Engine.EngineRunKind.Draft).ToListAsync(ct))
            .GroupBy(r => r.PartId).ToDictionary(g => g.Key, g => g.MaxBy(r => r.CreatedAt)!);
        var approvedIds = parts.Select(p => p.ApprovedRunId).OfType<Guid>().ToList();
        var approved = await db.Set<Engine.EngineRun>().AsNoTracking().Where(r => approvedIds.Contains(r.Id) && r.Summary != null)
            .ToDictionaryAsync(r => r.Id, r => r.Summary!, ct);

        var now = time.GetUtcNow();
        var result = new List<ClashPartSummary>(parts.Count);
        foreach (var part in parts)
        {
            var partModels = await models.OfPartAsync(part.Id, ct);
            var config = ConfigurationJson.Deserialize(part.DraftJson);
            var warnings = partModels
                .Select(m => (Label: m.Discipline ?? m.Name, m.Latest))
                .Where(m => Projects.ProjectEndpoints.IsStale(m.Latest, now))
                .Select(m => m.Latest is null ? $"{m.Label}: нет выгрузки" : $"{m.Label}: выгрузка от {m.Latest.ExtractedAt:dd.MM}")
                .ToList();
            result.Add(new ClashPartSummary(part.Id, part.Name, part.Object, part.Part, partModels.Count, config.Sets.Count, part.UpdatedAt,
                config.Groups.Select(g => g.Name).ToList(), warnings, runs.TryGetValue(part.Id, out var run) ? Engine.DraftRunSummary.Of(run) : null, part.ApprovedVersion,
                part.ApprovedRunId is { } approvedRun ? approved.GetValueOrDefault(approvedRun) : null));
        }

        return [.. result];
    }

    private static async Task<IResult> GetAsync(Guid id, PartModels models, ToadFlowDb db, CancellationToken ct)
    {
        var part = await db.Set<ClashPart>().AsNoTracking().SingleOrDefaultAsync(p => p.Id == id, ct);
        return part is null ? Results.NotFound() : Results.Ok(await ViewAsync(part, models, ct));
    }

    /// <summary>Модели, у которых разбор имени дал этот объект и эту часть здания, — кандидаты в часть.</summary>
    private static async Task<PartModelView[]> SuggestAsync(string? @object, string? part, PartModels models, CancellationToken ct) =>
        (await models.AllAsync(ct))
        .Where(m => Same(m.Classification.Object, @object) && Same(m.Classification.Part, part))
        .Select(m => m.View(null))
        .OrderBy(m => m.Name, StringComparer.OrdinalIgnoreCase)
        .ToArray();

    /// <summary>Часть сразу с моделями и стартовым черновиком: по разделам, из шаблона или копией другой части.</summary>
    private static async Task<IResult> CreateAsync(ClashPartCreateRequest request, ToadFlowDb db, HttpContext http, TimeProvider time, PartModels models,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Name)) return Results.BadRequest(new { error = "Укажите название части" });
        var members = (request.Models ?? []).DistinctBy(m => m.ModelId).ToList();
        var ids = members.Select(m => m.ModelId).ToList();
        if (await db.Set<ClashModel>().CountAsync(m => ids.Contains(m.Id), ct) != ids.Count)
            return Results.BadRequest(new { error = "Среди моделей есть неизвестная — обновите страницу" });

        ClashConfiguration? source = null;
        switch (request.Start?.Kind)
        {
            case null or "" or "empty" or "disciplines":
                break;
            case "template" when request.Start.TemplateId is { } templateId:
                source = await Templates.TemplateEndpoints.ConfigurationAsync(templateId, db, ct);
                if (source is null) return Results.BadRequest(new { error = "Шаблон не найден" });
                break;
            case "part" when request.Start.PartId is { } partId:
                var json = await db.Set<ClashPart>().AsNoTracking().Where(p => p.Id == partId).Select(p => p.DraftJson).SingleOrDefaultAsync(ct);
                if (json is null) return Results.BadRequest(new { error = "Часть-образец не найдена" });
                source = ConfigurationJson.Deserialize(json);
                break;
            default:
                return Results.BadRequest(new { error = "Не понятно, с чего начать черновик" });
        }

        var now = time.GetUtcNow();
        var part = new ClashPart { Id = Guid.CreateVersion7(), CreatedAt = now };
        Apply(part, new ClashPartRequest(request.Name, request.Object, request.Part), http, now);
        db.Set<ClashPart>().Add(part);
        db.Set<ClashPartModel>().AddRange(members.Select(m => new ClashPartModel
        {
            PartId = part.Id,
            ModelId = m.ModelId,
            DisciplineOverride = string.IsNullOrWhiteSpace(m.DisciplineOverride) ? null : m.DisciplineOverride.Trim()
        }));
        await db.SaveChangesAsync(ct);

        if (request.Start is { Kind: "disciplines" or "template" or "part" })
        {
            var (disciplines, refs) = await DraftEndpoints.ContextAsync(part.Id, models, ct);
            var config = source is null
                ? ConfigurationDefaults.ByDisciplines(disciplines)
                : DraftEndpoints.CopyFor(source, disciplines, refs.Select(m => m.ModelKey).ToList());
            await DraftEndpoints.StoreAsync(db, part, config, http, time, ct);
            await db.SaveChangesAsync(ct);
        }

        return Results.Created($"/api/clash/parts/{part.Id}", await ViewAsync(part, models, ct));
    }

    private static async Task<IResult> UpdateAsync(Guid id, ClashPartRequest request, ToadFlowDb db, HttpContext http, TimeProvider time,
        PartModels models, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Name)) return Results.BadRequest(new { error = "Укажите название части" });
        var part = await db.Set<ClashPart>().SingleOrDefaultAsync(p => p.Id == id, ct);
        if (part is null) return Results.NotFound();

        Apply(part, request, http, time.GetUtcNow());
        await db.SaveChangesAsync(ct);
        return Results.Ok(await ViewAsync(part, models, ct));
    }

    private static async Task<IResult> DeleteAsync(Guid id, ToadFlowDb db, ClashStorage storage, CancellationToken ct)
    {
        var deleted = await db.Set<ClashPart>().Where(p => p.Id == id).ExecuteDeleteAsync(ct);
        if (deleted > 0) storage.DeleteImageFolder(id);
        return deleted == 0 ? Results.NotFound() : Results.NoContent();
    }

    private static async Task<IResult> SetModelsAsync(Guid id, PartModelRequest[] request, ToadFlowDb db, HttpContext http, TimeProvider time,
        PartModels models, CancellationToken ct)
    {
        var part = await db.Set<ClashPart>().SingleOrDefaultAsync(p => p.Id == id, ct);
        if (part is null) return Results.NotFound();

        var ids = request.Select(m => m.ModelId).Distinct().ToList();
        var known = await db.Set<ClashModel>().Where(m => ids.Contains(m.Id)).Select(m => m.Id).ToListAsync(ct);
        if (known.Count != ids.Count) return Results.BadRequest(new { error = "Среди моделей есть неизвестная — обновите страницу" });

        await db.Set<ClashPartModel>().Where(m => m.PartId == id).ExecuteDeleteAsync(ct);
        db.Set<ClashPartModel>().AddRange(request.DistinctBy(m => m.ModelId).Select(m => new ClashPartModel
        {
            PartId = id,
            ModelId = m.ModelId,
            DisciplineOverride = string.IsNullOrWhiteSpace(m.DisciplineOverride) ? null : m.DisciplineOverride.Trim()
        }));
        part.UpdatedAt = time.GetUtcNow();
        part.UpdatedByUserId = http.User.UserId();
        await db.SaveChangesAsync(ct);
        return Results.Ok(await ViewAsync(part, models, ct));
    }

    private static void Apply(ClashPart part, ClashPartRequest request, HttpContext http, DateTimeOffset now)
    {
        part.Name = request.Name.Trim();
        part.Object = Clean(request.Object);
        part.Part = Clean(request.Part);
        part.UpdatedAt = now;
        part.UpdatedByUserId = http.User.UserId();
    }

    private static async Task<ClashPartView> ViewAsync(ClashPart part, PartModels models, CancellationToken ct) =>
        new(part.Id, part.Name, part.Object, part.Part, part.UpdatedAt, await models.OfPartAsync(part.Id, ct));

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static bool Same(string? actual, string? wanted) =>
        string.IsNullOrWhiteSpace(wanted) || string.Equals(actual, wanted.Trim(), StringComparison.OrdinalIgnoreCase);
}
