using System.Diagnostics;
using Alexpivo394.Revit.Clash.Rules;
using Shouldly;
using ToadFlow.Contracts;
using ToadFlow.Contracts.Clash;
using ToadFlow.Server.Clash.Engine;
using ToadFlow.Server.Clash.Parts;
using ToadFlow.Server.Tests.Infrastructure;
using static ToadFlow.Server.Tests.Infrastructure.ClashDraftFixtures;

namespace ToadFlow.Server.Tests;

public class ClashDraftResultTests : ServerTest
{
    /// <summary>Часть АР + ОВ после прогона с заданными коллизиями; наборы — «Все» каждого раздела.</summary>
    private async Task<(HttpClient Admin, Guid PartId, Guid SetAr, Guid SetOv, string ModelAr, string ModelOv)> RunAsync(Func<string, string, Guid, Guid, IReadOnlyList<EngineHit>> hits)
    {
        var admin = await AdminAsync();
        await using var agent = await FakeAgent.RegisterAsync(Server, admin);
        await agent.ConnectAsync("1.8.0", new InstalledRevitDto(2022, true));
        var partId = await PartAsync(Server, admin, agent, "POL_K1_AR.rvt", "POL_K1_OV.rvt");
        var draft = await admin.GetJsonAsync<DraftView>($"/api/clash/parts/{partId}/draft");
        var config = ConfigurationJson.Deserialize(draft.Configuration.GetRawText());
        var setAr = config.Sets.Single(s => s.Discipline == "АР").Id;
        var setOv = config.Sets.Single(s => s.Discipline == "ОВ").Id;
        var models = (await admin.GetJsonAsync<ClashPartView>($"/api/clash/parts/{partId}")).Models;
        var ar = models.Single(m => m.Discipline == "АР").ModelId.ToString("N");
        var ov = models.Single(m => m.Discipline == "ОВ").ModelId.ToString("N");

        var run = await (await admin.PostAsync($"/api/clash/parts/{partId}/draft/run", null)).ReadJsonAsync<DraftRunSummary>();
        var engine = EngineClient(Server);
        await engine.PostAsync("/api/engine/runs/claim", null);
        var all = hits(ar, ov, setAr, setOv);
        for (var part = 0; part * EngineApi.ResultChunkHits < Math.Max(1, all.Count); part++)
        {
            var chunk = all.Skip(part * EngineApi.ResultChunkHits).Take(EngineApi.ResultChunkHits).ToList();
            var last = (part + 1) * EngineApi.ResultChunkHits >= all.Count;
            (await PostResultAsync(engine, run.RunId, part, last, chunk, last ? Stats : null)).EnsureSuccessStatusCode();
        }

        return (admin, partId, setAr, setOv, ar, ov);
    }

    private static EngineHit Between(int i, string ma, string mb, string da, string db, Guid sa, Guid sb, float depth, string level = "Этаж 1") =>
        Hit(i, ma, mb, da, db, depth) with { SetsA = [sa], SetsB = [sb], LevelA = level, LevelB = level };

    [Test]
    public async Task Summary_ByDisciplinesAndSets()
    {
        var (admin, partId, setAr, setOv, _, _) = await RunAsync((ar, ov, sa, so) =>
        [
            Between(1, ar, ov, "АР", "ОВ", sa, so, 30),
            Between(2, ar, ov, "АР", "ОВ", sa, so, 40),
            Between(3, ov, ov, "ОВ", "ОВ", so, so, 50)
        ]);

        var summary = await admin.GetJsonAsync<DraftSummary>($"/api/clash/parts/{partId}/draft/summary");

        summary.Total.ShouldBe(3);
        var pairs = summary.ByDisciplines.ToDictionary(p => p.DisciplinePair);
        pairs["АР × ОВ"].Count.ShouldBe(2);
        pairs["АР × ОВ"].ToleranceMm.ShouldBe(20);
        pairs["ОВ × ОВ"].Count.ShouldBe(1);
        var sets = summary.BySets.Single(s => s.Count == 2);
        (sets.SetA, sets.SetB).ShouldBe((setAr, setOv));
        sets.SetAName.ShouldBe("Все");
    }

    [Test]
    public async Task Hits_FilterByDisciplinePairAndSet()
    {
        var (admin, partId, setAr, setOv, ar, _) = await RunAsync((ar, ov, sa, so) =>
        [
            Between(1, ar, ov, "АР", "ОВ", sa, so, 30, "Этаж 1"),
            Between(2, ar, ov, "АР", "ОВ", sa, so, 40, "Этаж 2"),
            Between(3, ov, ov, "ОВ", "ОВ", so, so, 50)
        ]);

        var byPair = await admin.GetJsonAsync<HitPage>($"/api/clash/parts/{partId}/draft/hits?disciplines={Uri.EscapeDataString("ОВ × ОВ")}");
        var bySets = await admin.GetJsonAsync<HitPage>($"/api/clash/parts/{partId}/draft/hits?setA={setOv}&setB={setAr}");
        var byLevel = await admin.GetJsonAsync<HitPage>($"/api/clash/parts/{partId}/draft/hits?level={Uri.EscapeDataString("этаж 2")}");
        var byModel = await admin.GetJsonAsync<HitPage>($"/api/clash/parts/{partId}/draft/hits?model={ar}&minDepth=35");

        byPair.Items.ShouldHaveSingleItem().Number.ShouldBe(3);
        bySets.Total.ShouldBe(2); // порядок наборов в фильтре не важен
        byLevel.Items.ShouldHaveSingleItem().Number.ShouldBe(2);
        var hit = byModel.Items.ShouldHaveSingleItem();
        hit.Number.ShouldBe(2);
        hit.ModelNameA.ShouldBe("POL_K1_AR.rvt");
        hit.SetNamesA.ShouldBe(["Все"]);
    }

    [Test]
    public async Task Hits_SortByDepthDescending()
    {
        var (admin, partId, _, _, _, _) = await RunAsync((ar, ov, sa, so) =>
            [Between(1, ar, ov, "АР", "ОВ", sa, so, 30), Between(2, ar, ov, "АР", "ОВ", sa, so, 90), Between(3, ar, ov, "АР", "ОВ", sa, so, 60)]);

        var byDepth = await admin.GetJsonAsync<HitPage>($"/api/clash/parts/{partId}/draft/hits?sort=depth");
        var byNumber = await admin.GetJsonAsync<HitPage>($"/api/clash/parts/{partId}/draft/hits");

        byDepth.Items.Select(h => h.DepthMm).ShouldBe([90f, 60f, 30f]);
        byNumber.Items.Select(h => h.Number).ShouldBe([1, 2, 3]);
    }

    [Test]
    public async Task Hits_PageSizeCapped()
    {
        var (admin, partId, _, _, _, _) = await RunAsync((ar, ov, sa, so) =>
            Enumerable.Range(1, 250).Select(i => Between(i, ar, ov, "АР", "ОВ", sa, so, i)).ToList());

        var page = await admin.GetJsonAsync<HitPage>($"/api/clash/parts/{partId}/draft/hits?pageSize=1000&page=2");

        page.Total.ShouldBe(250);
        page.Items.Count.ShouldBe(50); // не больше 200 на страницу: вторая страница по 200 — последние 50
        page.Items[0].Number.ShouldBe(201);
    }

    [Test]
    public async Task Summary_NoRunYet_Empty()
    {
        var admin = await AdminAsync();
        var part = await (await admin.PostJsonAsync("/api/clash/parts", new { name = "Пустая" })).ReadJsonAsync<ClashPartView>();

        (await admin.GetJsonAsync<DraftSummary>($"/api/clash/parts/{part.Id}/draft/summary")).Total.ShouldBe(0);
        (await admin.GetJsonAsync<HitPage>($"/api/clash/parts/{part.Id}/draft/hits")).Items.ShouldBeEmpty();
    }

    [Test]
    public async Task DraftResult_LargeHitCount_PagedAndSummarized()
    {
        var (admin, partId, _, _, _, _) = await RunAsync((ar, ov, sa, so) =>
            Enumerable.Range(1, 100_000).Select(i => Between(i, ar, ov, "АР", "ОВ", sa, so, i % 500)).ToList());
        await admin.GetJsonAsync<HitPage>($"/api/clash/parts/{partId}/draft/hits?pageSize=50"); // прогрев

        var page = Stopwatch.StartNew();
        var hits = await admin.GetJsonAsync<HitPage>($"/api/clash/parts/{partId}/draft/hits?pageSize=50&page=1000&sort=depth");
        page.Stop();
        var summarize = Stopwatch.StartNew();
        var summary = await admin.GetJsonAsync<DraftSummary>($"/api/clash/parts/{partId}/draft/summary");
        summarize.Stop();

        hits.Total.ShouldBe(100_000);
        hits.Items.Count.ShouldBe(50);
        summary.Total.ShouldBe(100_000);
        page.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(1));
        summarize.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(2));
    }
}
