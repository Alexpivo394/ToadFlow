using System.Text.Json;
using Alexpivo394.Revit.Clash.Geometry;
using Alexpivo394.Revit.Clash.Rules;
using ToadFlow.Server.Clash.Parts;

namespace ToadFlow.Server.Clash.Viewer;

/// <summary>
///     3D набора в редакторе: элементы набора из последних выгрузок моделей черновика, ближайшие к началу сцены, и окружение вокруг
///     выбранного образца. Тело — несохранённый черновик, как у предпросмотра числа: <c>{ configuration, setId, focus? }</c>.
///     Сцена и сетки считают выбор по отдельности одним и тем же детерминированным способом.
/// </summary>
public static class SetScene
{
    /// <summary>Сколько элементов набора рисуется.</summary>
    public const int MaxSetElements = 3000;

    /// <summary>Радиус окружения вокруг выбранного образца, мм.</summary>
    public const double ContextRadiusMm = 5000;

    /// <summary>
    ///     Сколько треугольников набора и окружения рисуется сеткой. Больше, чем у 3D коллизии: набор — до 3000 элементов, и при
    ///     400 тыс. треугольников трубы с изоляцией и фитинги уже через несколько сотен элементов оставались каркасом.
    /// </summary>
    public const long TriangleBudget = 1_500_000;

    private sealed record Body(ClashConfiguration Configuration, Guid SetId, ViewerKey? Focus, ViewerKey? Element);

    private sealed record Loaded(
        IResult? Error,
        IReadOnlyList<ViewerModel>? Models = null,
        IReadOnlyList<string>? Folders = null,
        IReadOnlyList<ViewerPick>? Picks = null,
        double X = 0,
        double Y = 0,
        double Z = 0,
        long MeshTriangles = 0,
        int Total = 0,
        bool Focused = false);

    public static async Task<IResult> SceneAsync(Guid id, HttpRequest request, PartModels models, ClashStorage storage, PartElementsCache cache,
        CancellationToken ct)
    {
        var loaded = await LoadAsync(id, request, models, storage, cache, ct);
        if (loaded.Error is { } error) return error;
        var elements = ViewerEndpoints.Elements(loaded.Models!, loaded.Picks!, loaded.X, loaded.Y, loaded.Z);
        return Results.Ok(new ViewerSceneView(Guid.Empty, 0, loaded.X, loaded.Y, loaded.Z, loaded.Focused ? ContextRadiusMm : 0, 0, elements,
            loaded.MeshTriangles, loaded.Total > MaxSetElements, loaded.Total));
    }

    public static async Task<IResult> MeshesAsync(Guid id, HttpRequest request, PartModels models, ClashStorage storage, PartElementsCache cache,
        HttpResponse response, CancellationToken ct)
    {
        var loaded = await LoadAsync(id, request, models, storage, cache, ct);
        if (loaded.Error is { } error) return error;
        await ViewerEndpoints.WriteMeshesAsync(loaded.Models!, loaded.Folders!, loaded.Picks!, loaded.X, loaded.Y, loaded.Z, response, ct);
        return Results.Empty;
    }

    /// <summary>«Почему попал»: тело <c>{ configuration, setId, model, uniqueId }</c>; по строке на каждую сработавшую группу условий.</summary>
    public static async Task<IResult> WhyAsync(Guid id, HttpRequest request, PartModels models, ClashStorage storage, PartElementsCache cache,
        CancellationToken ct)
    {
        if (Parse(await ReadAsync(request, ct)) is not { Element: { } element } body) return Results.BadRequest(new { error = "Передайте черновик, набор и элемент" });
        if (body.Configuration.Sets.FirstOrDefault(s => s.Id == body.SetId) is not { } set) return Results.BadRequest(new { error = "Набора нет в черновике" });

        var snapshots = await SnapshotsAsync(id, models, storage, cache, ct);
        var names = snapshots.ToDictionary(s => s.Model.Index.Header.ModelKey, s => s.Model.ModelName, StringComparer.OrdinalIgnoreCase);
        foreach (var snapshot in snapshots)
        {
            if (snapshot.Model.Index.Header.ModelKey != element.Model) continue;
            if (snapshot.Elements.Elements.FirstOrDefault(e => e.UniqueId == element.UniqueId) is not { } info) break;
            var input = new ElementsInput(snapshot.Model.Discipline, element.Model, [info], snapshot.Elements.Parameters);
            var matched = SetPreview.Count(body.Configuration, set.Id, [input]).ByAnyOf;
            return Results.Ok(set.AnyOf.Where((_, i) => matched[i] > 0).Select(g => ConditionText.Of(g, names)).ToList());
        }

        return Results.NotFound(new { error = "Элемента нет в выгрузке модели" });
    }

    private sealed record Snapshot(ViewerModel Model, string Folder, ElementsInput Elements);

    /// <summary>Последние выгрузки моделей части; модели без выгрузки или без раздела пропускаются.</summary>
    private static async Task<List<Snapshot>> SnapshotsAsync(Guid partId, PartModels models, ClashStorage storage, PartElementsCache cache, CancellationToken ct)
    {
        var result = new List<Snapshot>();
        foreach (var model in await models.OfPartAsync(partId, ct))
        {
            if (model is not { Discipline: { } discipline, Latest: { } latest }) continue;
            var folder = storage.SnapshotFolder(model.ModelId, latest.Id);
            var snapshot = cache.Get(latest.Id, folder);
            result.Add(new Snapshot(new ViewerModel(model.ModelId, model.Name, discipline, snapshot.Index), folder,
                new ElementsInput(discipline, snapshot.Index.Header.ModelKey, snapshot.Elements, snapshot.Parameters)));
        }

        return result;
    }

    private static async Task<Loaded> LoadAsync(Guid id, HttpRequest request, PartModels models, ClashStorage storage, PartElementsCache cache,
        CancellationToken ct)
    {
        if (Parse(await ReadAsync(request, ct)) is not { } body) return new Loaded(Results.BadRequest(new { error = "Передайте черновик и набор" }));
        var config = body.Configuration;
        if (config.Sets.FirstOrDefault(s => s.Id == body.SetId) is not { } set) return new Loaded(Results.BadRequest(new { error = "Набора нет в черновике" }));
        var group = config.Groups.FirstOrDefault(g => g.Id == set.GroupId);

        var snapshots = await SnapshotsAsync(id, models, storage, cache, ct);
        var viewerModels = snapshots.Select(s => s.Model).ToList();
        var membership = SetMembership.Evaluate(config, snapshots.Select(s => s.Elements).ToList());

        (int Model, int Element)? focus = null;
        var members = new List<(int Model, int Element)>();
        var others = new List<(int Model, int Element)>();
        for (var m = 0; m < snapshots.Count; m++)
        {
            var index = snapshots[m].Model.Index;
            var key = index.Header.ModelKey;
            var source = group is not null && group.Source.Includes(snapshots[m].Model.Discipline, key);
            for (var e = 0; e < index.Elements.Count; e++)
            {
                var uid = index.Elements[e].Info.UniqueId;
                if (body.Focus is { } wanted && wanted.Model == key && wanted.UniqueId == uid) focus = (m, e);
                if (!source) continue;
                if (membership.SetsOf(key, uid).Contains(set.Id)) members.Add((m, e));
                else others.Add((m, e));
            }
        }

        if (body.Focus is not null && focus is null) return new Loaded(Results.NotFound(new { error = "Элемента нет в выгрузке модели" }));
        var (x, y, z) = focus is { } f ? Center(viewerModels, [f]) : Center(viewerModels, members);

        var ordered = Nearest(viewerModels, members, x, y, z).Take(MaxSetElements).Select(p => (p.Model, p.Element, p.Distance, Role: ViewerRole.Set));
        var context = focus is null
            ? []
            : Nearest(viewerModels, others, x, y, z).TakeWhile(p => p.Distance <= ContextRadiusMm)
                .Take(ViewerScene.DefaultMaxElements - MaxSetElements).Select(p => (p.Model, p.Element, p.Distance, Role: ViewerRole.Context));

        var chosen = ordered.Concat(context).ToList();
        var triangles = chosen.Select(p =>
        {
            long count = viewerModels[p.Model].Index.Elements[p.Element].TriangleCount;
            return count < 0 ? ViewerScene.UnknownTriangles : count;
        }).ToList();
        var meshed = FitMeshes(triangles, TriangleBudget);
        var picks = chosen.Select((p, i) => new ViewerPick(p.Model, p.Element, p.Role, p.Distance, meshed[i])).ToList();
        var budget = triangles.Where((_, i) => meshed[i]).Sum();

        return new Loaded(null, viewerModels, snapshots.Select(s => s.Folder).ToList(), picks, x, y, z, budget, members.Count, focus is not null);
    }

    /// <summary>
    ///     Какие элементы рисуются сеткой: по порядку, пока влезают в бюджет. Не влезший в остаток элемент остаётся габаритом, а
    ///     следующие, поменьше, ещё берутся — иначе один крупный элемент оставлял каркасом все дальние.
    /// </summary>
    public static bool[] FitMeshes(IReadOnlyList<long> triangles, long budget)
    {
        var result = new bool[triangles.Count];
        long used = 0;
        for (var i = 0; i < triangles.Count; i++)
        {
            if (triangles[i] <= 0 || used + triangles[i] > budget) continue;
            used += triangles[i];
            result[i] = true;
        }

        return result;
    }

    /// <summary>Элементы по расстоянию от точки до габарита, при равенстве — по модели и UniqueId: выбор не зависит от порядка в слепке.</summary>
    private static IEnumerable<(int Model, int Element, double Distance)> Nearest(IReadOnlyList<ViewerModel> models, List<(int Model, int Element)> elements,
        double x, double y, double z) =>
        elements.Select(p => (p.Model, p.Element, Distance: Distance(models[p.Model], p.Element, x, y, z)))
            .OrderBy(p => p.Distance)
            .ThenBy(p => models[p.Model].Index.Header.ModelKey, StringComparer.Ordinal)
            .ThenBy(p => models[p.Model].Index.Elements[p.Element].Info.UniqueId, StringComparer.Ordinal);

    /// <summary>Центр общего габарита элементов в общей системе, мм; без элементов — начало.</summary>
    private static (double X, double Y, double Z) Center(IReadOnlyList<ViewerModel> models, IReadOnlyList<(int Model, int Element)> elements)
    {
        if (elements.Count == 0) return (0, 0, 0);
        double minX = double.MaxValue, minY = double.MaxValue, minZ = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue, maxZ = double.MinValue;
        foreach (var (m, e) in elements)
        {
            var header = models[m].Index.Header;
            var bounds = models[m].Index.Elements[e].Bounds;
            minX = Math.Min(minX, header.OriginX + bounds.Min.X);
            minY = Math.Min(minY, header.OriginY + bounds.Min.Y);
            minZ = Math.Min(minZ, header.OriginZ + bounds.Min.Z);
            maxX = Math.Max(maxX, header.OriginX + bounds.Max.X);
            maxY = Math.Max(maxY, header.OriginY + bounds.Max.Y);
            maxZ = Math.Max(maxZ, header.OriginZ + bounds.Max.Z);
        }

        return ((minX + maxX) / 2, (minY + maxY) / 2, (minZ + maxZ) / 2);
    }

    private static double Distance(ViewerModel model, int element, double x, double y, double z)
    {
        var header = model.Index.Header;
        var bounds = model.Index.Elements[element].Bounds;
        var dx = Gap(x, header.OriginX + bounds.Min.X, header.OriginX + bounds.Max.X);
        var dy = Gap(y, header.OriginY + bounds.Min.Y, header.OriginY + bounds.Max.Y);
        var dz = Gap(z, header.OriginZ + bounds.Min.Z, header.OriginZ + bounds.Max.Z);
        return Math.Sqrt(dx * dx + dy * dy + dz * dz);
    }

    private static double Gap(double value, double min, double max) => value < min ? min - value : value > max ? value - max : 0;

    private static async Task<string> ReadAsync(HttpRequest request, CancellationToken ct)
    {
        using var reader = new StreamReader(request.Body);
        return await reader.ReadToEndAsync(ct);
    }

    private static Body? Parse(string text)
    {
        try
        {
            using var document = JsonDocument.Parse(text);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("configuration", out var configuration)) return null;
            if (DraftEndpoints.TryParse(configuration.GetRawText()) is not { } config) return null;
            if (!root.TryGetProperty("setId", out var set) || set.ValueKind != JsonValueKind.String || !set.TryGetGuid(out var setId)) return null;

            static string? Text(JsonElement owner, string name) =>
                owner.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
            static ViewerKey? Key(JsonElement owner) => Text(owner, "model") is { } model && Text(owner, "uniqueId") is { } uid ? new ViewerKey(model, uid) : null;
            var focus = root.TryGetProperty("focus", out var raw) && raw.ValueKind == JsonValueKind.Object ? Key(raw) : null;
            return new Body(config, setId, focus, Key(root));
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
