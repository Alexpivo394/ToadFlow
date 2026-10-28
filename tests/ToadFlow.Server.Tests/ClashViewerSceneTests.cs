using System.Numerics;
using Alexpivo394.Revit.Clash.Geometry;
using Shouldly;
using ToadFlow.Server.Clash.Viewer;

namespace ToadFlow.Server.Tests;

public class ClashViewerSceneTests
{
    private static ModelIndexEntry Entry(string uid, float x, float y = 0, float z = 0, float size = 100, int triangles = 12) =>
        new(new ElementInfo(uid, 1, "OST_GenericModel", null, null, null, null, []),
            new Aabb(new Vector3(x, y, z), new Vector3(x + size, y + size, z + size)), 0, triangles, 0, 0);

    private static ViewerModel Model(string key, IEnumerable<ModelIndexEntry> entries, double ox = 0, double oy = 0, double oz = 0) =>
        new(Guid.NewGuid(), key, "АР", new ModelIndex(new ModelHeader(key, "p", 2022, DateTimeOffset.UnixEpoch, true, "v", ox, oy, oz), entries.ToArray(), []));

    [Test]
    public void Select_RadiusFiltersByBoxDistance()
    {
        // Точка коллизии — 50, 50, 50 внутри A; near начинается в 4.9 м от неё по X, far — в 5.1 м.
        var model = Model("m", [Entry("a", 0), Entry("b", 0), Entry("near", 4950), Entry("far", 5150)]);

        var selection = ViewerScene.Select([model], new ViewerKey("m", "a"), new ViewerKey("m", "b"), 50, 50, 50, 5000);

        selection.Picks.Select(p => model.Index.Elements[p.Element].Info.UniqueId).ShouldBe(["a", "b", "near"]);
        selection.Truncated.ShouldBeFalse();
    }

    [Test]
    public void Select_AAndBFirst_ThenByDistance()
    {
        var model = Model("m", [Entry("c3", 3000), Entry("b", 1000), Entry("c1", 200), Entry("a", 2000), Entry("c2", 500)]);

        var selection = ViewerScene.Select([model], new ViewerKey("m", "a"), new ViewerKey("m", "b"), 0, 0, 0, 5000);

        selection.Picks.Select(p => model.Index.Elements[p.Element].Info.UniqueId).ShouldBe(["a", "b", "c1", "c2", "c3"]);
        selection.Picks.Select(p => p.Role).ShouldBe([ViewerRole.A, ViewerRole.B, ViewerRole.Context, ViewerRole.Context, ViewerRole.Context]);
        selection.Picks[2].DistanceMm.ShouldBe(200, 0.001);
    }

    [Test]
    public void Select_TriangleBudget_RestAsBoxes()
    {
        var context = Enumerable.Range(0, 100).Select(i => Entry($"c{i:000}", 200 + i * 10, triangles: 10_000));
        var model = Model("m", [Entry("a", 0, triangles: 500_000), Entry("b", 0, triangles: 500_000), .. context]);

        var selection = ViewerScene.Select([model], new ViewerKey("m", "a"), new ViewerKey("m", "b"), 0, 0, 0, 5000);

        selection.Picks.Count.ShouldBe(102);
        selection.Picks.Take(2).ShouldAllBe(p => p.Mesh);
        selection.Picks.Skip(2).Count(p => p.Mesh).ShouldBe(40);
        selection.Picks.Skip(2).Take(40).ShouldAllBe(p => p.Mesh);
        selection.MeshTriangles.ShouldBe(1_400_000);
    }

    [Test]
    public void Select_MaxElements_Truncated()
    {
        var model = Model("m", [Entry("a", 0), Entry("b", 0), .. Enumerable.Range(0, 10).Select(i => Entry($"c{i}", 200 + i))]);

        var selection = ViewerScene.Select([model], new ViewerKey("m", "a"), new ViewerKey("m", "b"), 0, 0, 0, 5000, maxElements: 5);

        selection.Picks.Count.ShouldBe(5);
        selection.Truncated.ShouldBeTrue();
    }

    [Test]
    public void Select_UnknownTriangleCount_CountedAs1000()
    {
        var model = Model("m", [Entry("a", 0, triangles: -1), Entry("b", 0, triangles: -1), Entry("c1", 200, triangles: -1), Entry("c2", 300, triangles: -1),
            Entry("empty", 250, triangles: 0)]);

        var selection = ViewerScene.Select([model], new ViewerKey("m", "a"), new ViewerKey("m", "b"), 0, 0, 0, 5000, triangleBudget: 1500);

        selection.Picks.Select(p => (model.Index.Elements[p.Element].Info.UniqueId, p.Mesh))
            .ShouldBe([("a", true), ("b", true), ("c1", true), ("empty", false), ("c2", false)]);
    }

    [Test]
    public void Select_DifferentOrigins_DistanceInSharedCoordinates()
    {
        // Вторая модель сдвинута на 1 км: её элемент в своих координатах у нуля, в общих — в 1 км от коллизии.
        var first = Model("m1", [Entry("a", 0), Entry("b", 0)], ox: 7_000_000);
        var second = Model("m2", [Entry("shifted", 0), Entry("close", -1_000_000 + 300)], ox: 8_000_000);

        var selection = ViewerScene.Select([first, second], new ViewerKey("m1", "a"), new ViewerKey("m1", "b"), 7_000_000, 0, 0, 5000);

        selection.Picks.Select(p => (p.Model, second.Index.Elements.ElementAtOrDefault(p.Element)?.Info.UniqueId))
            .Skip(2).ShouldBe([(1, "close")]);
    }

    [Test]
    public void Select_MissingA_Throws()
    {
        var model = Model("m", [Entry("b", 0)]);

        Should.Throw<KeyNotFoundException>(() => ViewerScene.Select([model], new ViewerKey("m", "a"), new ViewerKey("m", "b"), 0, 0, 0, 5000));
    }
}
