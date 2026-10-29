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

    private static async Task<ClashPartSummary[]> ListAsync(ToadFlowDb db, CancellationToken ct)
    {
        var parts = await db.Set<ClashPart>().AsNoTracking().OrderBy(p => p.Name).ToListAsync(ct);
        var counts = await db.Set<ClashPartModel>().AsNoTracking().GroupBy(m => m.PartId).Select(g => new { g.Key, Count = g.Count() })
            .ToDictionaryAsync(g => g.Key, g => g.Count, ct);
        return parts.Select(p => new ClashPartSummary(p.Id, p.Name, p.Object, p.Part, counts.GetValueOrDefault(p.Id),
            ConfigurationJson.Deserialize(p.DraftJson).Sets.Count, p.UpdatedAt)).ToArray();
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
            DraftEndpoints.Store(part, config, http, time);
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

    private static async Task<IResult> DeleteAsync(Guid id, ToadFlowDb db, CancellationToken ct)
    {
        var deleted = await db.Set<ClashPart>().Where(p => p.Id == id).ExecuteDeleteAsync(ct);
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
