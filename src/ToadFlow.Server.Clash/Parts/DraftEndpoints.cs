using System.Text.Json;
using Alexpivo394.Revit.Clash.Rules;
using Microsoft.EntityFrameworkCore;
using ToadFlow.Server.Auth;
using ToadFlow.Server.Data;

namespace ToadFlow.Server.Clash.Parts;

/// <param name="Configuration">Черновик в формате <see cref="ConfigurationJson" />.</param>
/// <param name="Disciplines">Разделы моделей части по алфавиту.</param>
/// <param name="Problems">Ошибки черновика при нынешнем составе моделей: модели могли смениться после сохранения.</param>
public sealed record DraftView(JsonElement Configuration, IReadOnlyList<string> Disciplines, IReadOnlyList<string> Problems);

/// <summary>Черновик конфигурации части: наборы, матрица, исключения. Читает Наблюдатель, меняет Оператор.</summary>
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
            var config = ConfigurationJson.Deserialize(json);
            if (config.Sets is null || config.Matrix is null || config.Exclusions is null) return null;
            if (config.Sets.Any(s => s is null || s.AnyOf is null || s.Name is null || s.Discipline is null
                                     || s.AnyOf.Any(g => g?.All is null || g.All.Any(c => c?.Property is null))))
                return null;
            return config.Matrix.Any(c => c?.DisciplineA is null || c.DisciplineB is null) || config.Exclusions.Any(e => e is null) ? null : config;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Разделы моделей части по алфавиту; модели без раздела не в счёт.</summary>
    public static async Task<IReadOnlyList<string>> DisciplinesAsync(Guid partId, PartModels models, CancellationToken ct) =>
        (await models.OfPartAsync(partId, ct)).Select(m => m.Discipline).OfType<string>()
        .Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToList();

    public static async Task<DraftView> ViewAsync(ClashPart part, PartModels models, CancellationToken ct)
    {
        var disciplines = await DisciplinesAsync(part.Id, models, ct);
        var config = ConfigurationJson.Deserialize(part.DraftJson);
        using var document = JsonDocument.Parse(part.DraftJson);
        return new DraftView(document.RootElement.Clone(), disciplines, ConfigurationValidator.Problems(config, disciplines.ToArray()));
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

        var problems = ConfigurationValidator.Problems(config, (await DisciplinesAsync(id, models, ct)).ToArray());
        if (problems.Count > 0) return Results.BadRequest(new { error = string.Join("; ", problems), problems });

        Store(part, config, request.HttpContext, time);
        await db.SaveChangesAsync(ct);
        return Results.Ok(await ViewAsync(part, models, ct));
    }

    /// <summary>Добавляет клетки для всех пар разделов части (и раздела с самим собой): включены, 20 мм. Заданные клетки не меняются.</summary>
    private static async Task<IResult> FillMatrixAsync(Guid id, HttpContext http, ToadFlowDb db, PartModels models, TimeProvider time, CancellationToken ct)
    {
        var part = await db.Set<ClashPart>().SingleOrDefaultAsync(p => p.Id == id, ct);
        if (part is null) return Results.NotFound();

        var config = ConfigurationJson.Deserialize(part.DraftJson);
        var disciplines = await DisciplinesAsync(id, models, ct);
        var matrix = config.Matrix.ToList();
        for (var i = 0; i < disciplines.Count; i++)
        for (var j = i; j < disciplines.Count; j++)
            if (config.CellFor(disciplines[i], disciplines[j]) is null)
                matrix.Add(new MatrixCell(disciplines[i], disciplines[j], true, DefaultToleranceMm));

        Store(part, config with { Matrix = matrix }, http, time);
        await db.SaveChangesAsync(ct);
        return Results.Ok(await ViewAsync(part, models, ct));
    }

    public static void Store(ClashPart part, ClashConfiguration config, HttpContext http, TimeProvider time)
    {
        part.DraftJson = ConfigurationJson.Serialize(config);
        part.UpdatedAt = time.GetUtcNow();
        part.UpdatedByUserId = http.User.UserId();
    }
}
