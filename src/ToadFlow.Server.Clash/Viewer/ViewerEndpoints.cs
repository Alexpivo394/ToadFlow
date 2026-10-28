using Alexpivo394.Revit.Clash.Geometry;
using Microsoft.EntityFrameworkCore;
using ToadFlow.Server.Clash.Engine;
using ToadFlow.Server.Clash.Parts;
using ToadFlow.Server.Data;

namespace ToadFlow.Server.Clash.Viewer;

/// <param name="Role"><c>A</c>, <c>B</c> или <c>context</c>.</param>
/// <param name="ModelId">Id модели в форме N.</param>
/// <param name="Min">Габарит, мм относительно точки коллизии.</param>
/// <param name="Mesh">Сетка есть в ответе <c>/meshes</c>; иначе элемент показывается габаритом.</param>
public sealed record ViewerElementView(
    int Index,
    string Role,
    string ModelId,
    string ModelName,
    string Discipline,
    string UniqueId,
    long ElementId,
    string Category,
    string? Family,
    string? Type,
    string? Level,
    float[] Min,
    float[] Max,
    int Triangles,
    bool Mesh);

/// <param name="X">Точка коллизии в общей системе, мм; начало координат сцены.</param>
public sealed record ViewerSceneView(
    Guid RunId,
    int Number,
    double X,
    double Y,
    double Z,
    double RadiusMm,
    float DepthMm,
    IReadOnlyList<ViewerElementView> Elements,
    long MeshTriangles,
    bool Truncated);

public sealed record ViewerParameter(string Name, string? Text, double? Number);

public sealed record ViewerElementProperties(
    string ModelName,
    string Discipline,
    string UniqueId,
    long ElementId,
    string Category,
    string? Family,
    string? Type,
    string? Level,
    string? Workset,
    IReadOnlyList<ViewerParameter> Parameters);

/// <summary>
///     Просмотрщик коллизии: сцена вокруг коллизии, её сетки и свойства элементов. Всё берётся из слепков, на которых шёл
///     прогон, — иначе координаты коллизии не сошлись бы с геометрией. Прогон должен быть видимым результатом своей части.
/// </summary>
public static class ViewerEndpoints
{
    private const string Stale = "результат черновика обновился или коллизия не найдена";

    public static void MapClashViewer(this RouteGroupBuilder api)
    {
        var run = api.MapGroup("/clash/runs/{runId:guid}");
        run.MapGet("/hits/{number:int}/scene", SceneAsync);
        run.MapGet("/hits/{number:int}/meshes", MeshesAsync);
        run.MapGet("/elements/{modelId}/{uniqueId}", ElementAsync);
    }

    private static async Task<IResult> SceneAsync(Guid runId, int number, double? radius, ToadFlowDb db, ClashStorage storage, PartElementsCache cache,
        CancellationToken ct)
    {
        var loaded = await LoadAsync(runId, number, radius, db, storage, cache, ct);
        if (loaded.Error is { } error) return error;
        var (models, hit, selection, r) = (loaded.Models!, loaded.Hit!, loaded.Selection!, loaded.Radius);

        var elements = selection.Picks.Select((pick, i) =>
        {
            var model = models[pick.Model];
            var header = model.Index.Header;
            var entry = model.Index.Elements[pick.Element];
            var info = entry.Info;
            float[] Relative(System.Numerics.Vector3 v) =>
                [(float)(header.OriginX + v.X - hit.X), (float)(header.OriginY + v.Y - hit.Y), (float)(header.OriginZ + v.Z - hit.Z)];
            return new ViewerElementView(i, pick.Role switch { ViewerRole.A => "A", ViewerRole.B => "B", _ => "context" }, header.ModelKey, model.ModelName,
                model.Discipline, info.UniqueId, info.ElementId, info.Category, info.Family, info.Type, info.Level, Relative(entry.Bounds.Min),
                Relative(entry.Bounds.Max), entry.TriangleCount, pick.Mesh);
        }).ToList();
        return Results.Ok(new ViewerSceneView(runId, number, hit.X, hit.Y, hit.Z, r, hit.DepthMm, elements, selection.MeshTriangles, selection.Truncated));
    }

    /// <summary>Сетки пишутся в ответ по одной: сцена в несколько сотен тысяч треугольников не собирается в памяти целиком.</summary>
    private static async Task<IResult> MeshesAsync(Guid runId, int number, double? radius, ToadFlowDb db, ClashStorage storage, PartElementsCache cache,
        HttpResponse response, CancellationToken ct)
    {
        var loaded = await LoadAsync(runId, number, radius, db, storage, cache, ct);
        if (loaded.Error is { } error) return error;
        var (models, hit, selection) = (loaded.Models!, loaded.Hit!, loaded.Selection!);

        var picks = selection.Picks.Select((pick, i) => (Pick: pick, Index: i)).Where(p => p.Pick.Mesh).ToList();
        response.ContentType = "application/octet-stream";
        await response.Body.WriteAsync(ViewerMeshWriter.Header(picks.Count), ct);
        var files = new Dictionary<int, FileStream>();
        try
        {
            foreach (var (pick, index) in picks)
            {
                var model = models[pick.Model];
                if (!files.TryGetValue(pick.Model, out var file))
                    files[pick.Model] = file = File.OpenRead(Path.Combine(loaded.Folders![pick.Model], "model.tcg"));
                var mesh = ModelGeometryFile.ReadMesh(file, model.Index.Elements[pick.Element]);
                await response.Body.WriteAsync(ViewerMeshWriter.Encode(index, mesh, model.Index.Header, hit.X, hit.Y, hit.Z), ct);
            }
        }
        finally
        {
            foreach (var file in files.Values) await file.DisposeAsync();
        }

        return Results.Empty;
    }

    private static async Task<IResult> ElementAsync(Guid runId, string modelId, string uniqueId, ToadFlowDb db, ClashStorage storage, PartElementsCache cache,
        CancellationToken ct)
    {
        if (await VisibleRunAsync(runId, db, ct) is not { } run) return Results.NotFound(new { error = Stale });
        if (run.Models.FirstOrDefault(m => m.ModelId.ToString("N") == modelId) is not { } model)
            return Results.NotFound(new { error = "модели нет в прогоне" });
        var folder = storage.SnapshotFolder(model.ModelId, model.SnapshotId);
        if (!File.Exists(Path.Combine(folder, "model.tcg"))) return Gone();

        var snapshot = cache.Get(model.SnapshotId, folder);
        if (snapshot.Index.Elements.FirstOrDefault(e => e.Info.UniqueId == uniqueId)?.Info is not { } info)
            return Results.NotFound(new { error = "элемента нет в выгрузке модели" });
        var parameters = snapshot.Parameters.For(uniqueId)
            .Select(p => new ViewerParameter(p.Name, p.Text, p.Number))
            .OrderBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
        return Results.Ok(new ViewerElementProperties(model.ModelName, model.Discipline, info.UniqueId, info.ElementId, info.Category, info.Family,
            info.Type, info.Level, info.Workset, parameters));
    }

    private sealed record Loaded(
        IResult? Error,
        IReadOnlyList<ViewerModel>? Models = null,
        IReadOnlyList<string>? Folders = null,
        ClashDraftHit? Hit = null,
        ViewerSelection? Selection = null,
        double Radius = 0);

    private static async Task<Loaded> LoadAsync(Guid runId, int number, double? radius, ToadFlowDb db, ClashStorage storage, PartElementsCache cache,
        CancellationToken ct)
    {
        var r = radius ?? ViewerScene.DefaultRadiusMm;
        if (r is < ViewerScene.MinRadiusMm or > ViewerScene.MaxRadiusMm)
            return new Loaded(Results.BadRequest(new { error = $"Радиус — от {ViewerScene.MinRadiusMm} до {ViewerScene.MaxRadiusMm} мм" }));
        if (await VisibleRunAsync(runId, db, ct) is not { } run) return new Loaded(Results.NotFound(new { error = Stale }));
        var hit = await db.Set<ClashDraftHit>().AsNoTracking().SingleOrDefaultAsync(h => h.RunId == runId && h.Number == number, ct);
        if (hit is null) return new Loaded(Results.NotFound(new { error = Stale }));

        var models = new List<ViewerModel>(run.Models.Count);
        var folders = new List<string>(run.Models.Count);
        foreach (var model in run.Models)
        {
            var folder = storage.SnapshotFolder(model.ModelId, model.SnapshotId);
            if (!File.Exists(Path.Combine(folder, "model.tcg"))) return new Loaded(Gone());
            models.Add(new ViewerModel(model.ModelId, model.ModelName, model.Discipline, cache.Get(model.SnapshotId, folder).Index));
            folders.Add(folder);
        }

        try
        {
            var selection = ViewerScene.Select(models, new ViewerKey(hit.ModelA, hit.UniqueIdA), new ViewerKey(hit.ModelB, hit.UniqueIdB), hit.X, hit.Y,
                hit.Z, r);
            return new Loaded(null, models, folders, hit, selection, r);
        }
        catch (KeyNotFoundException exception)
        {
            return new Loaded(Results.NotFound(new { error = exception.Message }));
        }
    }

    private static Task<EngineRun?> VisibleRunAsync(Guid runId, ToadFlowDb db, CancellationToken ct) =>
        db.Set<EngineRun>().AsNoTracking()
            .Where(r => r.Id == runId && db.Set<ClashPart>().Any(p => p.ResultRunId == r.Id))
            .SingleOrDefaultAsync(ct);

    private static IResult Gone() =>
        Results.Json(new { error = "выгрузка модели удалена, прогоните черновик заново" }, statusCode: StatusCodes.Status410Gone);
}
