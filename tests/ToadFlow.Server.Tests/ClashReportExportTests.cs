using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Text.RegularExpressions;
using System.Xml;
using Alexpivo394.Revit.Clash.Geometry;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using ToadFlow.Contracts;
using ToadFlow.Contracts.Clash;
using ToadFlow.Server.Clash;
using ToadFlow.Server.Clash.Collisions;
using ToadFlow.Server.Clash.Images;
using ToadFlow.Server.Clash.Parts;
using ToadFlow.Server.Clash.Reports;
using ToadFlow.Server.Data.Entities;
using ToadFlow.Server.Tests.Infrastructure;
using static ToadFlow.Server.Tests.Infrastructure.ClashDraftFixtures;

namespace ToadFlow.Server.Tests;

public class ClashReportExportTests : ServerTest
{
    private const string Base = "http://toadflow.local";
    private const string Nbsp = " ";

    /// <summary>Пара коллизий <see cref="ClashDraftFixtures.Hit" /> по умолчанию — HTML выгружается по паре.</summary>
    private static readonly string Pair = "&pair=" + Uri.EscapeDataString("АР × ОВ");
    private static readonly byte[] TinyPng = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 1, 2, 3, 4];

    private async Task<(HttpClient Admin, FakeAgent Agent, Guid PartId, Guid RunId)> RunAsync(IReadOnlyList<EngineHit> hits)
    {
        var admin = await AdminAsync();
        var agent = await FakeAgent.RegisterAsync(Server, admin);
        await agent.ConnectAsync("1.8.0", new InstalledRevitDto(2022, true));
        var partId = await PartAsync(Server, admin, agent, "POL_K1_AR.rvt", "POL_K1_OV.rvt");
        var engine = EngineClient(Server);
        var approve = await admin.PostJsonAsync($"/api/clash/parts/{partId}/approve", new { comment = (string?)null, run = true });
        var runId = (await approve.ReadJsonAsync<ApprovedVersionView>()).RunId.ShouldNotBeNull();
        (await engine.PostAsync("/api/engine/runs/claim", null)).EnsureSuccessStatusCode();
        var chunks = hits.Chunk(EngineApi.ResultChunkHits).ToList();
        for (var i = 0; i < chunks.Count; i++)
        {
            var response = await PostApprovedResultAsync(engine, runId, i, i == chunks.Count - 1, chunks[i], Stats, [], []);
            response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        }

        return (admin, agent, partId, runId);
    }

    private static ElementInfo Info(string uid) => new(uid, uid.Length, "OST_GenericModel", "Семейство", "Тип", "Этаж 1", null, []);

    /// <summary>
    ///     Часть АР + ОВ на настоящих слепках (кубы <c>a1…aN</c> и <c>b1…bN</c> через 1 м, ОВ сдвинуты на 50 мм) и утверждённый прогон
    ///     из <paramref name="count" /> пар. <paramref name="uniqueIdA" /> подменяет элемент A у пары. Картинок в кеше нет.
    /// </summary>
    private async Task<(HttpClient Admin, FakeAgent Agent, Guid PartId, Guid RunId)> RenderableRunAsync(int count, Func<int, string?>? uniqueIdA = null)
    {
        var admin = await AdminAsync();
        var agent = await FakeAgent.RegisterAsync(Server, admin);
        await agent.ConnectAsync("1.8.0", new InstalledRevitDto(2022, true));
        ModelGeometry Geometry(string name, string key)
        {
            var isAr = name.Contains("_AR");
            var elements = Enumerable.Range(1, count)
                .Select(i => ClashFixtures.Box(Info((isAr ? "a" : "b") + i), (i - 1) * 1000 + (isAr ? 0 : 50), 0, 0, 100)).ToArray();
            return new ModelGeometry(new ModelHeader(key, name, 2022, DateTimeOffset.UtcNow, true, "Navisworks", 0, 0, 0), elements,
                elements.Select(e => e.Info.UniqueId).ToArray());
        }

        var partId = await PartAsync(Server, admin, agent, ["POL_K1_AR.rvt", "POL_K1_OV.rvt"], Geometry);
        var models = (await admin.GetJsonAsync<ClashPartView>($"/api/clash/parts/{partId}")).Models;
        var ar = models.Single(m => m.Discipline == "АР").ModelId.ToString("N");
        var ov = models.Single(m => m.Discipline == "ОВ").ModelId.ToString("N");
        var hits = Enumerable.Range(1, count).Select(i => Hit(i, ar, ov) with
        {
            UniqueIdA = uniqueIdA?.Invoke(i) ?? "a" + i, UniqueIdB = "b" + i, X = (i - 1) * 1000 + 75.0, Y = 50, Z = 50, DepthMm = 50
        }).ToList();

        var engine = EngineClient(Server);
        var approve = await admin.PostJsonAsync($"/api/clash/parts/{partId}/approve", new { comment = (string?)null, run = true });
        var runId = (await approve.ReadJsonAsync<ApprovedVersionView>()).RunId.ShouldNotBeNull();
        (await engine.PostAsync("/api/engine/runs/claim", null)).EnsureSuccessStatusCode();
        var response = await PostApprovedResultAsync(engine, runId, 0, true, hits, Stats, [], []);
        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        return (admin, agent, partId, runId);
    }

    private static IReadOnlyList<EngineHit> Hits(int count) => Enumerable.Range(1, count).Select(i => Hit(i) with { FingerprintA = 11, FingerprintB = 22 }).ToList();

    /// <summary>Картинки всем коллизиям части в кеше — как после отрисовки по запросу.</summary>
    private async Task ImagesAsync(Guid partId)
    {
        using var scope = Server.Services.CreateScope();
        var storage = scope.ServiceProvider.GetRequiredService<ClashStorage>();
        var collisions = await DbAsync(db => db.Set<ClashCollision>().AsNoTracking().Where(c => c.PartId == partId).ToListAsync());
        foreach (var c in collisions)
            await storage.WriteImageAsync(partId, c.Id, ImageTarget.VersionOf(c), new MemoryStream(TinyPng), CancellationToken.None);
    }

    private static List<List<string>> SheetRows(byte[] xlsx, int sheet = 1)
    {
        using var zip = new ZipArchive(new MemoryStream(xlsx), ZipArchiveMode.Read);
        using var reader = XmlReader.Create(zip.GetEntry($"xl/worksheets/sheet{sheet}.xml")!.Open());
        var rows = new List<List<string>>();
        while (reader.Read())
        {
            if (reader is { NodeType: XmlNodeType.Element, LocalName: "row" }) rows.Add([]);
            else if (reader is { NodeType: XmlNodeType.Element, LocalName: "c" })
            {
                // Пустые ячейки писатель пропускает: место ячейки — по букве столбца в адресе r.
                var column = reader.GetAttribute("r")!.TakeWhile(char.IsLetter).Aggregate(0, (n, ch) => n * 26 + ch - 'A' + 1);
                while (rows[^1].Count < column) rows[^1].Add("");
            }
            else if (reader is { NodeType: XmlNodeType.Element, LocalName: "f" }) rows[^1][^1] = "=" + reader.ReadElementContentAsString();
            else if (reader is { NodeType: XmlNodeType.Element, LocalName: "t" or "v" } && !rows[^1][^1].StartsWith('='))
                rows[^1][^1] = reader.ReadElementContentAsString();
        }

        return rows;
    }

    [Test]
    public async Task ExportXlsx_ColumnsAndRows_MatchReport()
    {
        var (admin, agent, partId, runId) = await RunAsync(Hits(3));
        await using var _ = agent;

        var response = await admin.GetAsync($"/api/clash/reports/{runId}/export.xlsx?scope=changes&baseUrl={Base}");

        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        response.Content.Headers.ContentType!.MediaType.ShouldBe("application/vnd.openxmlformats-officedocument.spreadsheetml.sheet");
        var rows = SheetRows(await response.Content.ReadAsByteArrayAsync());
        rows[0].ShouldBe(ReportColumns.Headers);
        rows.Count.ShouldBe(1 + 3);
        var first = rows[1];
        string Cell(string header) => first[ReportColumns.Headers.ToList().IndexOf(header)];
        Cell("№").ShouldBe("1");
        Cell("Статус").ShouldBe("новая");
        Cell("Переход в прогоне").ShouldBe("новая");
        Cell("Категория A").ShouldBe("Стены");
        Cell("Пара разделов").ShouldBe("АР × ОВ");
        var id = (await admin.GetJsonAsync<ReportPage>($"/api/clash/reports/{runId}/collisions?q=1")).Items.Single().Collision.Id;
        Cell("Ссылка").ShouldContain($"{Base}/clash/checks/{partId}?collision={id}");
        SheetRows(await response.Content.ReadAsByteArrayAsync(), 2).ShouldContain(r => r.Count > 1 && r[0] == "Новых" && r[1] == "3");
    }

    [Test]
    [Explicit]
    public async Task Rows_60k_PageUnder300ms()
    {
        var hits = Hits(60_000).Select(h => h with { LevelA = $"Этаж {h.ElementIdA % 25}", LevelB = $"Этаж {h.ElementIdA % 25}", Z = h.ElementIdA % 25 * 3000 })
            .ToList();
        var (admin, agent, _, runId) = await RunAsync(hits);
        await using var __ = agent;
        var query = $"/api/clash/reports/{runId}/collisions?scope=open&page=300&pageSize=100";
        await admin.GetJsonAsync<ReportPage>(query);

        var watch = Stopwatch.StartNew();
        var page = await admin.GetJsonAsync<ReportPage>(query);
        var pageTime = watch.Elapsed;
        var levels = await admin.GetJsonAsync<List<ReportLevelView>>($"/api/clash/reports/{runId}/levels?scope=open");

        Console.WriteLine($"page: {pageTime.TotalMilliseconds:0} ms, page + levels: {watch.ElapsedMilliseconds} ms");
        page.Items.Count.ShouldBe(100);
        levels.Count.ShouldBe(25);
        pageTime.ShouldBeLessThan(TimeSpan.FromMilliseconds(300));
    }

    [Test]
    [Explicit]
    public async Task ExportXlsx_60kRows_StreamedAllRows()
    {
        var (admin, agent, _, runId) = await RunAsync(Hits(60_000));
        await using var __ = agent;
        GC.Collect();
        var before = GC.GetTotalMemory(true);
        long peak = 0;
        using var stop = new CancellationTokenSource();
        var sampler = Task.Run(async () =>
        {
            while (!stop.IsCancellationRequested)
            {
                peak = Math.Max(peak, GC.GetTotalMemory(false));
                await Task.Delay(20);
            }
        });

        var watch = Stopwatch.StartNew();
        var file = Path.GetTempFileName();
        using (var response = await admin.GetAsync($"/api/clash/reports/{runId}/export.xlsx?scope=changes&baseUrl={Base}", HttpCompletionOption.ResponseHeadersRead))
        {
            response.StatusCode.ShouldBe(HttpStatusCode.OK);
            await using var target = File.Create(file);
            await (await response.Content.ReadAsStreamAsync()).CopyToAsync(target);
        }

        var elapsed = watch.Elapsed;
        await stop.CancelAsync();
        await sampler;
        var growth = peak - before;
        Console.WriteLine($"Excel 60 тыс. строк: {elapsed.TotalSeconds:0.0} с, {new FileInfo(file).Length / 1024.0 / 1024:0.0} МБ, прирост управляемой памяти {growth / 1024.0 / 1024:0} МБ");
        SheetRows(await File.ReadAllBytesAsync(file)).Count.ShouldBe(60_001);
        growth.ShouldBeLessThan(200L * 1024 * 1024);
        File.Delete(file);
    }

    [Test]
    public async Task ExportHtml_SelfContained_ImagesInline()
    {
        var (admin, agent, partId, runId) = await RunAsync([Hit(1) with { FamilyA = "<script>alert(1)</script>" }, Hit(2)]);
        await using var _ = agent;
        await ImagesAsync(partId);

        var response = await admin.GetAsync($"/api/clash/reports/{runId}/export.html?baseUrl={Base}{Pair}");

        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        response.Content.Headers.ContentType!.MediaType.ShouldBe("text/html");
        var html = await response.Content.ReadAsStringAsync();
        Regex.Matches(html, "data:image/png;base64,").Count.ShouldBe(2);
        // Скрипт в файле — только свой, фильтры; текст из модели экранирован.
        Regex.Matches(html, "<script").Count.ShouldBe(1);
        html.ShouldNotContain("<script>alert");
        html.ShouldContain("&lt;script&gt;alert(1)&lt;/script&gt;");
        foreach (Match link in Regex.Matches(html, "(?:src|href)=\"([^\"]*)\""))
            (link.Groups[1].Value.StartsWith("data:") || link.Groups[1].Value.StartsWith(Base + "/") || link.Groups[1].Value.StartsWith('#')).ShouldBeTrue(link.Value);
        html.ShouldContain("@media print");
        html.ShouldNotContain("картинки — у первых");
    }

    [Test]
    public async Task ExportHtml_NoPair_400()
    {
        var (admin, agent, _, runId) = await RunAsync(Hits(2));
        await using var _ = agent;

        var response = await admin.GetAsync($"/api/clash/reports/{runId}/export.html?baseUrl={Base}");

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync()).ShouldContain("архивом");
    }

    [Test]
    public async Task ExportHtml_1001Rows_1000CardsAndOneTableRow()
    {
        var (admin, agent, partId, runId) = await RunAsync(Hits(1001));
        await using var _ = agent;
        await ImagesAsync(partId);

        var html = await admin.GetStringAsync($"/api/clash/reports/{runId}/export.html?baseUrl={Base}{Pair}");

        ReportHtml.MaxImages.ShouldBe(1000);
        Regex.Matches(html, "<div class=\"card\"").Count.ShouldBe(1000);
        Regex.Matches(html, "data:image/png;base64,").Count.ShouldBe(1000);
        Regex.Matches(html, "<tr class=\"row\"").Count.ShouldBe(1);
        html.ShouldContain($"картинки — у первых 1{Nbsp}000 из 1{Nbsp}001");
    }

    [Test]
    public async Task ExportHtml_Exactly1000_NoTable()
    {
        var (admin, agent, partId, runId) = await RunAsync(Hits(1000));
        await using var _ = agent;
        await ImagesAsync(partId);

        var html = await admin.GetStringAsync($"/api/clash/reports/{runId}/export.html?baseUrl={Base}{Pair}");

        Regex.Matches(html, "<div class=\"card\"").Count.ShouldBe(1000);
        html.ShouldNotContain("<tr class=\"row\"");
        html.ShouldNotContain("картинки — у первых");
    }

    [Test]
    public async Task ExportHtml_LevelsTocAndGroupsInOrder()
    {
        var (admin, agent, _, runId) = await RunAsync(ClashReportTests.LevelHits());
        await using var _ = agent;

        var html = await admin.GetStringAsync($"/api/clash/reports/{runId}/export.html?baseUrl={Base}&pair={Uri.EscapeDataString("ВК × ОВ")}");
        // Образец для ручной проверки фильтров в браузере.
        var sample = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "toadclash-report-sample")).FullName;
        await File.WriteAllTextAsync(Path.Combine(sample, "pair-levels.html"), html);

        html.ShouldContain("ОВ × ВК");
        Regex.Matches(html, "<a href=\"#level-(\\d+)\">([^<]*)</a> <span class=\"count\">(\\d+)</span>").Select(m => (m.Groups[2].Value, m.Groups[3].Value))
            .ShouldBe([("Этаж 1", "2"), ("Этаж 2", "1"), ("Кровля", "1"), ("Этаж 10", "1"), ("Без уровня", "1")]);
        Regex.Matches(html, "<h3 class=\"level\" id=\"level-\\d+\" data-level=\"([^\"]*)\"").Select(m => m.Groups[1].Value)
            .ShouldBe(["Этаж 1", "Этаж 2", "Кровля", "Этаж 10", ""]);
        Regex.Matches(html, "<div class=\"card\" data-status=\"(\\w+)\" data-level=\"([^\"]*)\" data-q=\"([^\"]*)\"")
            .Select(m => m.Groups[3].Value.Split(' ')[0]).ShouldBe(["2", "6", "1", "3", "4", "5"]);
        html.ShouldContain("data-status=\"New\" data-level=\"Этаж 1\"");
    }

    /// <summary>«Показать в Revit» у каждой строки: те же две строки, что копирует карточка веба, — номер с частью и ссылка.</summary>
    [Test]
    public async Task ReportHtml_RowHasShowInRevitText()
    {
        var (admin, agent, partId, runId) = await RunAsync(Hits(2));
        await using var _ = agent;
        var name = (await admin.GetJsonAsync<ClashPartView>($"/api/clash/parts/{partId}")).Name;

        var html = await admin.GetStringAsync($"/api/clash/reports/{runId}/export.html?baseUrl={Base}{Pair}");

        var clips = Regex.Matches(html, "data-clip=\"ToadClash №([0-9]+) —([^\"]*?)&#10;([^\"]*)\"");
        clips.Count.ShouldBe(2);
        foreach (Match clip in clips)
        {
            clip.Groups[2].Value.Trim().ShouldBe(name);
            clip.Groups[3].Value.ShouldStartWith($"{Base}/clash/checks/{partId}?collision=");
        }

        Regex.Matches(html, ">Показать в Revit</button>").Count.ShouldBe(2);
        html.ShouldContain("navigator.clipboard");
        Regex.Matches(html, "<script").Count.ShouldBe(1);
    }

    [Test]
    public async Task ExportHtml_FilterScript_Inline()
    {
        var (admin, agent, _, runId) = await RunAsync(Hits(2));
        await using var _ = agent;

        var html = await admin.GetStringAsync($"/api/clash/reports/{runId}/export.html?baseUrl={Base}{Pair}");

        Regex.Matches(html, "<script>").Count.ShouldBe(1);
        html.ShouldNotContain("<script src");
        foreach (var id in new[] { "f-q", "f-level", "f-status", "shown" }) html.ShouldContain($"id=\"{id}\"");
        html.ShouldContain("Показано 2 из 2");
    }

    [Test]
    public async Task ExportXlsx_RowsInReportOrder_PairLabel()
    {
        var (admin, agent, _, runId) = await RunAsync(ClashReportTests.LevelHits());
        await using var _ = agent;

        var rows = SheetRows(await (await admin.GetAsync($"/api/clash/reports/{runId}/export.xlsx?scope=changes&baseUrl={Base}")).Content.ReadAsByteArrayAsync());

        var headers = ReportColumns.Headers.ToList();
        rows.Skip(1).Select(r => r[headers.IndexOf("№")]).ShouldBe(["7", "2", "6", "1", "3", "4", "5"]);
        rows[2][headers.IndexOf("Пара разделов")].ShouldBe("ОВ × ВК");
    }

    [Test]
    public async Task ExportHtml_RendersMissingImages()
    {
        var (admin, agent, _, runId) = await RenderableRunAsync(3);
        await using var _ = agent;
        var images = Server.Services.GetRequiredService<CollisionImages>();
        var before = images.Renders;

        var html = await admin.GetStringAsync($"/api/clash/reports/{runId}/export.html?baseUrl={Base}{Pair}");

        Regex.Matches(html, "data:image/png;base64,").Count.ShouldBe(3);
        (images.Renders - before).ShouldBe(3);
    }

    [Test]
    public async Task ExportHtml_MissingElement_RowWithoutImage()
    {
        var (admin, agent, _, runId) = await RenderableRunAsync(3, i => i == 2 ? "missing" : null);
        await using var _ = agent;

        var response = await admin.GetAsync($"/api/clash/reports/{runId}/export.html?baseUrl={Base}{Pair}");

        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        var html = await response.Content.ReadAsStringAsync();
        Regex.Matches(html, "data:image/png;base64,").Count.ShouldBe(2);
        Regex.Matches(html, "нет картинки").Count.ShouldBe(1);
    }

    /// <summary>
    ///     Картинки рисуются окном впереди записи: строк больше окна (2 × потоки), часть картинок в кеше, у одной строки элемента
    ///     нет. Каждая картинка — у своей строки.
    /// </summary>
    [Test]
    public async Task ExportHtml_MoreRowsThanRenderWindow_ImagesOnTheirRows()
    {
        const int count = 7;
        var (admin, agent, partId, runId) = await RenderableRunAsync(count, i => i == 3 ? "missing" : null);
        await using var _ = agent;
        await using var limited = Server.WithWebHostBuilder(builder => builder.UseSetting("Clash:ImageThreads", "1"));
        var client = limited.CreateClient();
        client.DefaultRequestHeaders.Authorization = admin.DefaultRequestHeaders.Authorization;
        var collisions = await DbAsync(db => db.Set<ClashCollision>().AsNoTracking().Where(c => c.PartId == partId).ToListAsync());
        byte[] Cached(int number) => [..TinyPng, (byte)number];
        foreach (var c in collisions.Where(c => c.Number is 5 or 6))
            await ClashStorage.WriteImageAsync(Server.ClashStorageRoot, partId, c.Id, ImageTarget.VersionOf(c), new MemoryStream(Cached(c.Number)), CancellationToken.None);

        var response = await client.GetAsync($"/api/clash/reports/{runId}/export.html?baseUrl={Base}{Pair}");

        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        var cards = Regex.Matches(await response.Content.ReadAsStringAsync(),
            "<div class=\"card\"[^>]*>(?:<img [^>]*src=\"data:image/png;base64,(?<png>[^\"]+)\">|<div class=\"noimg\">).*?<b>№(?<number>\\d+)</b>", RegexOptions.Singleline);
        cards.Select(m => int.Parse(m.Groups["number"].Value)).Order().ShouldBe(Enumerable.Range(1, count));
        foreach (Match card in cards)
        {
            var number = int.Parse(card.Groups["number"].Value);
            var png = card.Groups["png"].Success ? Convert.FromBase64String(card.Groups["png"].Value) : null;
            if (number == 3) png.ShouldBeNull();
            else if (number is 5 or 6) png.ShouldBe(Cached(number));
            else png.ShouldNotBeNull($"№{number}").Length.ShouldBeGreaterThan(1000);
        }

        limited.Services.GetRequiredService<CollisionImages>().Renders.ShouldBe(count - 3);
    }

    [Test]
    public async Task ExportHtml_CorruptGeometry_RowsWithoutImages()
    {
        var (admin, agent, _, runId) = await RenderableRunAsync(2);
        await using var _ = agent;
        var models = await DbAsync(db => db.Set<Clash.Engine.EngineRun>().AsNoTracking().Where(r => r.Id == runId).Select(r => r.Models).SingleAsync());
        foreach (var model in models)
        {
            await using var file = new FileStream(Path.Combine(Server.ClashStorageRoot, model.ModelId.ToString("N"), model.SnapshotId.ToString("N"), "model.tcg"),
                FileMode.Open, FileAccess.Write);
            file.SetLength(16);
        }

        var response = await admin.GetAsync($"/api/clash/reports/{runId}/export.html?baseUrl={Base}{Pair}");

        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        var html = await response.Content.ReadAsStringAsync();
        Regex.Matches(html, "data:image/png;base64,").Count.ShouldBe(0);
        Regex.Matches(html, "нет картинки").Count.ShouldBe(2);
    }

    [Test]
    public async Task ExportXlsx_10kRows_StreamedAllRows()
    {
        var (admin, agent, _, runId) = await RunAsync(Hits(10_000));
        await using var __ = agent;
        GC.Collect();
        var before = GC.GetTotalMemory(true);
        long peak = 0;
        using var stop = new CancellationTokenSource();
        var sampler = Task.Run(async () =>
        {
            while (!stop.IsCancellationRequested)
            {
                peak = Math.Max(peak, GC.GetTotalMemory(false));
                await Task.Delay(20);
            }
        });

        var file = Path.GetTempFileName();
        using (var response = await admin.GetAsync($"/api/clash/reports/{runId}/export.xlsx?scope=changes&baseUrl={Base}", HttpCompletionOption.ResponseHeadersRead))
        {
            response.StatusCode.ShouldBe(HttpStatusCode.OK);
            await using var target = File.Create(file);
            await (await response.Content.ReadAsStreamAsync()).CopyToAsync(target);
        }

        await stop.CancelAsync();
        await sampler;
        var growth = peak - before;
        SheetRows(await File.ReadAllBytesAsync(file)).Count.ShouldBe(10_001);
        growth.ShouldBeLessThan(64L * 1024 * 1024);
        File.Delete(file);
    }

    [Test]
    public async Task Export_Designer_Ok()
    {
        var (_, agent, partId, runId) = await RunAsync(Hits(1));
        await using var __ = agent;
        var designer = await ClientAsync(UserRole.Designer, "ВК");

        // Проектировщик смотрит отчёт, старые прогоны и выгружает; запускать прогоны не может (Runs_DesignerAndViewer_Forbidden).
        foreach (var path in new[]
                 {
                     $"/api/clash/reports/{runId}", $"/api/clash/reports/{runId}/collisions", $"/api/clash/reports/{runId}/levels",
                     $"/api/clash/parts/{partId}/approved/runs"
                 })
            (await designer.GetAsync(path)).StatusCode.ShouldBe(HttpStatusCode.OK, path);
        (await designer.GetAsync($"/api/clash/reports/{runId}/export.xlsx?baseUrl={Base}")).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await designer.GetAsync($"/api/clash/reports/{runId}/export.html?baseUrl={Base}{Pair}")).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Test]
    public async Task Export_BadBaseUrl_400()
    {
        var (admin, agent, _, runId) = await RunAsync(Hits(1));
        await using var _ = agent;

        (await admin.GetAsync($"/api/clash/reports/{runId}/export.xlsx")).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await admin.GetAsync($"/api/clash/reports/{runId}/export.html?baseUrl=javascript:alert(1)")).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await admin.GetAsync($"/api/clash/reports/{Guid.NewGuid()}/export.xlsx?baseUrl={Base}")).StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Test]
    public async Task Export_FileNameHeader_Rfc5987()
    {
        var (admin, agent, partId, runId) = await RunAsync(Hits(1));
        await using var _ = agent;
        var part = await DbAsync(db => db.Set<ClashPart>().SingleAsync(p => p.Id == partId));

        var response = await admin.GetAsync($"/api/clash/reports/{runId}/export.xlsx?baseUrl={Base}");

        var disposition = response.Content.Headers.ContentDisposition.ShouldNotBeNull();
        disposition.DispositionType.ShouldBe("attachment");
        var name = disposition.FileNameStar.ShouldNotBeNull();
        name.ShouldStartWith($"Коллизии {part.Name} ");
        name.ShouldMatch(@" \d{2}\.\d{2}\.\d{4}\.xlsx$");
        response.Content.Headers.GetValues("Content-Disposition").Single().ShouldContain("filename*=UTF-8''");
    }
}
