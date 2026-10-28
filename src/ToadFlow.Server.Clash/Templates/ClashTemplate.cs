using Alexpivo394.Revit.Clash.Rules;
using Microsoft.EntityFrameworkCore;
using ToadFlow.Server.Auth;
using ToadFlow.Server.Clash.Parts;
using ToadFlow.Server.Data;

namespace ToadFlow.Server.Clash.Templates;

/// <summary>Шаблон проверки: наборы, матрица и исключения, снятые с черновика части, — чтобы применить их к другой части.</summary>
public sealed class ClashTemplate
{
    public Guid Id { get; set; }
    public string Name { get; set; } = "";
    public string ConfigurationJson { get; set; } = "";
    public Guid UpdatedByUserId { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

public sealed record ClashTemplateRequest(string Name, Guid PartId);

public sealed record ClashTemplateView(Guid Id, string Name, int Sets, IReadOnlyList<string> Disciplines, DateTimeOffset UpdatedAt);

public static class TemplateEndpoints
{
    public static void MapClashTemplates(this RouteGroupBuilder api)
    {
        var templates = api.MapGroup("/clash/templates");
        templates.MapGet("", ListAsync);

        var write = templates.MapGroup("").RequireAuthorization(Policies.Operator);
        write.MapPost("", CreateAsync);
        write.MapDelete("/{id:guid}", DeleteAsync);
        write.MapPost("/{id:guid}/apply/{partId:guid}", ApplyAsync);
    }

    private static async Task<ClashTemplateView[]> ListAsync(ToadFlowDb db, CancellationToken ct) =>
        (await db.Set<ClashTemplate>().AsNoTracking().OrderBy(t => t.Name).ToListAsync(ct)).Select(View).ToArray();

    private static async Task<IResult> CreateAsync(ClashTemplateRequest request, ToadFlowDb db, HttpContext http, TimeProvider time, CancellationToken ct)
    {
        var name = request.Name?.Trim() ?? "";
        if (name.Length == 0) return Results.BadRequest(new { error = "Укажите название шаблона" });
        var part = await db.Set<ClashPart>().AsNoTracking().SingleOrDefaultAsync(p => p.Id == request.PartId, ct);
        if (part is null) return Results.NotFound();
        var names = await db.Set<ClashTemplate>().Select(t => t.Name).ToListAsync(ct);
        if (names.Contains(name, StringComparer.OrdinalIgnoreCase)) return Results.Conflict(new { error = $"Шаблон «{name}» уже есть" });

        var template = new ClashTemplate
        {
            Id = Guid.CreateVersion7(), Name = name, ConfigurationJson = part.DraftJson, UpdatedAt = time.GetUtcNow(), UpdatedByUserId = http.User.UserId()
        };
        db.Set<ClashTemplate>().Add(template);
        await db.SaveChangesAsync(ct);
        return Results.Created($"/api/clash/templates/{template.Id}", View(template));
    }

    private static async Task<IResult> DeleteAsync(Guid id, ToadFlowDb db, CancellationToken ct) =>
        await db.Set<ClashTemplate>().Where(t => t.Id == id).ExecuteDeleteAsync(ct) == 0 ? Results.NotFound() : Results.NoContent();

    /// <summary>
    ///     Заменяет черновик части копией шаблона: наборы получают новые Id (исключения переводятся на них), клетки матрицы
    ///     остаются только для разделов части. Наборы чужих разделов остаются — их покажут проблемы черновика.
    /// </summary>
    private static async Task<IResult> ApplyAsync(Guid id, Guid partId, ToadFlowDb db, PartModels models, HttpContext http, TimeProvider time,
        CancellationToken ct)
    {
        var template = await db.Set<ClashTemplate>().AsNoTracking().SingleOrDefaultAsync(t => t.Id == id, ct);
        var part = await db.Set<ClashPart>().SingleOrDefaultAsync(p => p.Id == partId, ct);
        if (template is null || part is null) return Results.NotFound();

        var source = Alexpivo394.Revit.Clash.Rules.ConfigurationJson.Deserialize(template.ConfigurationJson);
        var disciplines = new HashSet<string>(await DraftEndpoints.DisciplinesAsync(partId, models, ct), StringComparer.OrdinalIgnoreCase);
        var ids = source.Sets.ToDictionary(s => s.Id, _ => Guid.NewGuid());
        var config = source with
        {
            Sets = source.Sets.Select(s => s with { Id = ids[s.Id] }).ToList(),
            Matrix = source.Matrix.Where(c => disciplines.Contains(c.DisciplineA) && disciplines.Contains(c.DisciplineB)).ToList(),
            Exclusions = source.Exclusions.Where(e => ids.ContainsKey(e.SetA) && ids.ContainsKey(e.SetB))
                .Select(e => new SetPairExclusion(ids[e.SetA], ids[e.SetB])).ToList()
        };

        DraftEndpoints.Store(part, config, http, time);
        await db.SaveChangesAsync(ct);
        return Results.Ok(await DraftEndpoints.ViewAsync(part, models, ct));
    }

    private static ClashTemplateView View(ClashTemplate template)
    {
        var config = Alexpivo394.Revit.Clash.Rules.ConfigurationJson.Deserialize(template.ConfigurationJson);
        return new ClashTemplateView(template.Id, template.Name, config.Sets.Count,
            config.Sets.Select(s => s.Discipline).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToList(),
            template.UpdatedAt);
    }
}
