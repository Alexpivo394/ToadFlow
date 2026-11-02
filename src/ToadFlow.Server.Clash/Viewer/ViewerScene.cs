using Alexpivo394.Revit.Clash.Geometry;

namespace ToadFlow.Server.Clash.Viewer;

/// <summary>Модель прогона для просмотрщика: оглавление её слепка и раздел.</summary>
public sealed record ViewerModel(Guid ModelId, string ModelName, string Discipline, ModelIndex Index);

/// <summary>Элемент коллизии: ключ модели в слепке (Id модели в форме N) и UniqueId.</summary>
public sealed record ViewerKey(string Model, string UniqueId);

public enum ViewerRole
{
    A,
    B,
    Set,
    Context
}

/// <param name="Model">Индекс модели во входном списке.</param>
/// <param name="Element">Индекс элемента в оглавлении модели.</param>
/// <param name="DistanceMm">Расстояние от точки коллизии до габарита элемента; 0 — точка внутри.</param>
/// <param name="Mesh">Элемент грузится сеткой; иначе показывается габаритом.</param>
public sealed record ViewerPick(int Model, int Element, ViewerRole Role, double DistanceMm, bool Mesh);

/// <param name="MeshTriangles">Сколько треугольников у элементов с сеткой.</param>
/// <param name="Truncated">Окружение не влезло в лимит элементов, дальние отброшены.</param>
public sealed record ViewerSelection(IReadOnlyList<ViewerPick> Picks, long MeshTriangles, bool Truncated);

/// <summary>
///     Что показать вокруг коллизии: A и B целиком, окружение в радиусе — ближние сеткой до бюджета треугольников, дальше
///     габаритами. Выбор детерминирован: манифест сцены и ответ сеток считаются им по отдельности и должны совпасть.
/// </summary>
public static class ViewerScene
{
    public const double DefaultRadiusMm = 5000;
    public const double MinRadiusMm = 1000;
    public const double MaxRadiusMm = 20_000;
    public const long DefaultTriangleBudget = 400_000;
    public const int DefaultMaxElements = 20_000;

    /// <summary>Так считается элемент слепка до версии 3, где число треугольников не записано.</summary>
    public const int UnknownTriangles = 1000;

    /// <param name="x">Точка коллизии в общей системе, мм.</param>
    /// <exception cref="KeyNotFoundException">A или B нет в оглавлениях моделей.</exception>
    public static ViewerSelection Select(IReadOnlyList<ViewerModel> models, ViewerKey a, ViewerKey b, double x, double y, double z, double radiusMm,
        long triangleBudget = DefaultTriangleBudget, int maxElements = DefaultMaxElements)
    {
        (int Model, int Element)? foundA = null, foundB = null;
        var context = new List<(int Model, int Element, double Distance)>();
        for (var m = 0; m < models.Count; m++)
        {
            var index = models[m].Index;
            var header = index.Header;
            for (var e = 0; e < index.Elements.Count; e++)
            {
                var entry = index.Elements[e];
                if (header.ModelKey == a.Model && entry.Info.UniqueId == a.UniqueId) foundA = (m, e);
                else if (header.ModelKey == b.Model && entry.Info.UniqueId == b.UniqueId) foundB = (m, e);
                else
                {
                    var distance = Distance(entry.Bounds, header, x, y, z);
                    if (distance <= radiusMm) context.Add((m, e, distance));
                }
            }
        }

        if (foundA is not { } pa) throw new KeyNotFoundException($"Элемент A {a.UniqueId} не найден в слепках прогона");
        if (foundB is not { } pb) throw new KeyNotFoundException($"Элемент B {b.UniqueId} не найден в слепках прогона");

        context.Sort((l, r) =>
        {
            var byDistance = l.Distance.CompareTo(r.Distance);
            if (byDistance != 0) return byDistance;
            var byModel = string.CompareOrdinal(models[l.Model].Index.Header.ModelKey, models[r.Model].Index.Header.ModelKey);
            return byModel != 0
                ? byModel
                : string.CompareOrdinal(models[l.Model].Index.Elements[l.Element].Info.UniqueId, models[r.Model].Index.Elements[r.Element].Info.UniqueId);
        });

        var picks = new List<ViewerPick>
        {
            new(pa.Model, pa.Element, ViewerRole.A, Distance(models, pa, x, y, z), true),
            new(pb.Model, pb.Element, ViewerRole.B, Distance(models, pb, x, y, z), true)
        };
        var meshTriangles = Triangles(models, pa) + Triangles(models, pb);
        long budget = 0;
        var meshing = true;
        var limit = Math.Max(0, maxElements - picks.Count);
        foreach (var (m, e, distance) in context.Take(limit))
        {
            var triangles = Triangles(models, (m, e));
            var mesh = false;
            if (meshing && triangles > 0)
            {
                if (budget + triangles <= triangleBudget)
                {
                    budget += triangles;
                    mesh = true;
                }
                else meshing = false;
            }

            picks.Add(new ViewerPick(m, e, ViewerRole.Context, distance, mesh));
        }

        return new ViewerSelection(picks, meshTriangles + budget, context.Count > limit);
    }

    private static long Triangles(IReadOnlyList<ViewerModel> models, (int Model, int Element) at)
    {
        var count = models[at.Model].Index.Elements[at.Element].TriangleCount;
        return count < 0 ? UnknownTriangles : count;
    }

    private static double Distance(IReadOnlyList<ViewerModel> models, (int Model, int Element) at, double x, double y, double z) =>
        Distance(models[at.Model].Index.Elements[at.Element].Bounds, models[at.Model].Index.Header, x, y, z);

    private static double Distance(Aabb bounds, ModelHeader header, double x, double y, double z)
    {
        var dx = Gap(x, header.OriginX + bounds.Min.X, header.OriginX + bounds.Max.X);
        var dy = Gap(y, header.OriginY + bounds.Min.Y, header.OriginY + bounds.Max.Y);
        var dz = Gap(z, header.OriginZ + bounds.Min.Z, header.OriginZ + bounds.Max.Z);
        return Math.Sqrt(dx * dx + dy * dy + dz * dz);
    }

    private static double Gap(double value, double min, double max) => value < min ? min - value : value > max ? value - max : 0;
}
