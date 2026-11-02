using Alexpivo394.Revit.Clash.Geometry;
using Microsoft.EntityFrameworkCore;
using ToadFlow.Server.Clash.Collisions;
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
/// <param name="Total">Сцена набора: сколько элементов в наборе; у сцены коллизии — 0.</param>
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
    bool Truncated,
    int Total = 0);

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
        CancellationToken ct) =>
        Scene(await LoadAsync(runId, number, radius, db, storage, cache, ct), runId, number);

    private static async Task<IResult> MeshesAsync(Guid runId, int number, double? radius, ToadFlowDb db, ClashStorage storage, PartElementsCache cache,
        HttpResponse response, CancellationToken ct) =>
        await MeshesOf(await LoadAsync(runId, number, radius, db, storage, cache, ct), response, ct);

    /// <summary>Сцена коллизии утверждённой проверки — по прогону, который нашёл её последним.</summary>
    public static async Task<IResult> CollisionSceneAsync(Guid id, double? radius, ToadFlowDb db, ClashStorage storage, PartElementsCache cache,
        CancellationToken ct)
    {
        var (loaded, collision) = await LoadCollisionAsync(id, radius, db, storage, cache, ct);
        return collision is null ? loaded.Error! : Scene(loaded, collision.LastFoundRunId, collision.Number);
    }

    public static async Task<IResult> CollisionMeshesAsync(Guid id, double? radius, ToadFlowDb db, ClashStorage storage, PartElementsCache cache,
        HttpResponse response, CancellationToken ct) =>
        await MeshesOf((await LoadCollisionAsync(id, radius, db, storage, cache, ct)).Loaded, response, ct);

    private static IResult Scene(Loaded loaded, Guid runId, int number)
    {
        if (loaded.Error is { } error) return error;
        var (models, hit, selection, r) = (loaded.Models!, loaded.Hit!, loaded.Selection!, loaded.Radius);
        var elements = Elements(models, selection.Picks, hit.X, hit.Y, hit.Z);
        return Results.Ok(new ViewerSceneView(runId, number, hit.X, hit.Y, hit.Z, r, hit.DepthMm, elements, selection.MeshTriangles, selection.Truncated));
    }

    /// <summary>Элементы сцены в порядке выбора; габариты — мм относительно начала сцены.</summary>
    /// <param name="x">Начало сцены в общей системе, мм.</param>
    internal static List<ViewerElementView> Elements(IReadOnlyList<ViewerModel> models, IReadOnlyList<ViewerPick> picks, double x, double y, double z) =>
        picks.Select((pick, i) =>
        {
            var model = models[pick.Model];
            var header = model.Index.Header;
            var entry = model.Index.Elements[pick.Element];
            var info = entry.Info;
            float[] Relative(System.Numerics.Vector3 v) =>
                [(float)(header.OriginX + v.X - x), (float)(header.OriginY + v.Y - y), (float)(header.OriginZ + v.Z - z)];
            var role = pick.Role switch { ViewerRole.A => "A", ViewerRole.B => "B", ViewerRole.Set => "set", _ => "context" };
            return new ViewerElementView(i, role, header.ModelKey, model.ModelName, model.Discipline, info.UniqueId, info.ElementId, info.Category,
                info.Family, info.Type, info.Level, Relative(entry.Bounds.Min), Relative(entry.Bounds.Max), entry.TriangleCount, pick.Mesh);
        }).ToList();

    private static async Task<IResult> MeshesOf(Loaded loaded, HttpResponse response, CancellationToken ct)
    {
        if (loaded.Error is { } error) return error;
        await WriteMeshesAsync(loaded.Models!, loaded.Folders!, loaded.Selection!.Picks, loaded.Hit!.X, loaded.Hit.Y, loaded.Hit.Z, response, ct);
        return Results.Empty;
    }

    /// <summary>
    ///     Сетки выбранных элементов с <see cref="ViewerPick.Mesh" /> в формате <see cref="ViewerMeshWriter" />. Пишутся в ответ по одной:
    ///     сцена в несколько сотен тысяч треугольников не собирается в памяти целиком.
    /// </summary>
    /// <param name="folders">Папки слепков, по одной на модель из <paramref name="models" />.</param>
    /// <param name="x">Начало сцены в общей системе, мм.</param>
    internal static async Task WriteMeshesAsync(IReadOnlyList<ViewerModel> models, IReadOnlyList<string> folders, IReadOnlyList<ViewerPick> selected,
        double x, double y, double z, HttpResponse response, CancellationToken ct)
    {
        var picks = selected.Select((pick, i) => (Pick: pick, Index: i)).Where(p => p.Pick.Mesh).ToList();
        response.ContentType = "application/octet-stream";
        await response.Body.WriteAsync(ViewerMeshWriter.Header(picks.Count), ct);
        var files = new Dictionary<int, FileStream>();
        try
        {
            foreach (var (pick, index) in picks)
            {
                var model = models[pick.Model];
                if (!files.TryGetValue(pick.Model, out var file))
                    files[pick.Model] = file = File.OpenRead(Path.Combine(folders[pick.Model], "model.tcg"));
                var mesh = ModelGeometryFile.ReadMesh(file, model.Index.Elements[pick.Element]);
                await response.Body.WriteAsync(ViewerMeshWriter.Encode(index, mesh, model.Index.Header, x, y, z), ct);
            }
        }
        finally
        {
            foreach (var file in files.Values) await file.DisposeAsync();
        }
    }

    private static async Task<IResult> ElementAsync(Guid runId, string modelId, string uniqueId, ToadFlowDb db, ClashStorage storage, PartElementsCache cache,
        CancellationToken ct) =>
        await VisibleRunAsync(runId, db, ct) is { } run
            ? Element(run, modelId, uniqueId, storage, cache)
            : Results.NotFound(new { error = Stale });

    /// <summary>Свойства элемента 3D коллизии утверждённой проверки — из слепков прогона, который нашёл её последним.</summary>
    public static async Task<IResult> CollisionElementAsync(Guid id, string modelId, string uniqueId, ToadFlowDb db, ClashStorage storage,
        PartElementsCache cache, CancellationToken ct)
    {
        var runId = await db.Set<ClashCollision>().AsNoTracking().Where(c => c.Id == id).Select(c => (Guid?)c.LastFoundRunId).SingleOrDefaultAsync(ct);
        if (runId is null) return Results.NotFound(new { error = "коллизия не найдена" });
        var run = await db.Set<EngineRun>().AsNoTracking().SingleOrDefaultAsync(r => r.Id == runId, ct);
        return run is null ? Gone() : Element(run, modelId, uniqueId, storage, cache);
    }

    private static IResult Element(EngineRun run, string modelId, string uniqueId, ClashStorage storage, PartElementsCache cache)
    {
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

    /// <summary>Пара элементов и точка коллизии, вокруг которой собирается сцена.</summary>
    private sealed record Target(string ModelA, string UniqueIdA, string ModelB, string UniqueIdB, double X, double Y, double Z, float DepthMm);

    private sealed record Loaded(
        IResult? Error,
        IReadOnlyList<ViewerModel>? Models = null,
        IReadOnlyList<string>? Folders = null,
        Target? Hit = null,
        ViewerSelection? Selection = null,
        double Radius = 0);

    private static async Task<Loaded> LoadAsync(Guid runId, int number, double? radius, ToadFlowDb db, ClashStorage storage, PartElementsCache cache,
        CancellationToken ct)
    {
        if (await VisibleRunAsync(runId, db, ct) is not { } run) return new Loaded(Results.NotFound(new { error = Stale }));
        var hit = await db.Set<ClashDraftHit>().AsNoTracking().SingleOrDefaultAsync(h => h.RunId == runId && h.Number == number, ct);
        if (hit is null) return new Loaded(Results.NotFound(new { error = Stale }));
        return LoadTarget(run, new Target(hit.ModelA, hit.UniqueIdA, hit.ModelB, hit.UniqueIdB, hit.X, hit.Y, hit.Z, hit.DepthMm), radius, storage, cache);
    }

    private static async Task<(Loaded Loaded, ClashCollision? Collision)> LoadCollisionAsync(Guid id, double? radius, ToadFlowDb db, ClashStorage storage,
        PartElementsCache cache, CancellationToken ct)
    {
        var collision = await db.Set<ClashCollision>().AsNoTracking().SingleOrDefaultAsync(c => c.Id == id, ct);
        if (collision is null) return (new Loaded(Results.NotFound(new { error = "коллизия не найдена" })), null);
        var run = await db.Set<EngineRun>().AsNoTracking().SingleOrDefaultAsync(r => r.Id == collision.LastFoundRunId, ct);
        if (run is null) return (new Loaded(Gone()), null);
        var loaded = LoadTarget(run,
            new Target(collision.ModelA, collision.UniqueIdA, collision.ModelB, collision.UniqueIdB, collision.X, collision.Y, collision.Z, collision.DepthMm),
            radius, storage, cache);
        return (loaded, loaded.Error is null ? collision : null);
    }

    private static Loaded LoadTarget(EngineRun run, Target hit, double? radius, ClashStorage storage, PartElementsCache cache)
    {
        var r = radius ?? ViewerScene.DefaultRadiusMm;
        if (r is < ViewerScene.MinRadiusMm or > ViewerScene.MaxRadiusMm)
            return new Loaded(Results.BadRequest(new { error = $"Радиус — от {ViewerScene.MinRadiusMm} до {ViewerScene.MaxRadiusMm} мм" }));

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

    /// <summary>Прогон, чьи коллизии видны: результат черновика части или прогон, последним нашедший коллизию утверждённой проверки.</summary>
    private static Task<EngineRun?> VisibleRunAsync(Guid runId, ToadFlowDb db, CancellationToken ct) =>
        db.Set<EngineRun>().AsNoTracking()
            .Where(r => r.Id == runId && (db.Set<ClashPart>().Any(p => p.ResultRunId == r.Id || p.ApprovedRunId == r.Id)
                                          || db.Set<ClashCollision>().Any(c => c.LastFoundRunId == r.Id)))
            .SingleOrDefaultAsync(ct);

    private static IResult Gone() =>
        Results.Json(new { error = "выгрузка модели удалена: сцену этого прогона уже не показать" }, statusCode: StatusCodes.Status410Gone);
}
