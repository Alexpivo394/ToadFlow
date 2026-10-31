using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Text.RegularExpressions;
using System.Xml;
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

        var response = await admin.GetAsync($"/api/clash/reports/{runId}/export.html?baseUrl={Base}");

        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        response.Content.Headers.ContentType!.MediaType.ShouldBe("text/html");
        var html = await response.Content.ReadAsStringAsync();
        Regex.Matches(html, "data:image/png;base64,").Count.ShouldBe(2);
        html.ShouldNotContain("<script");
        html.ShouldContain("&lt;script&gt;alert(1)&lt;/script&gt;");
        foreach (Match link in Regex.Matches(html, "(?:src|href)=\"([^\"]*)\""))
            (link.Groups[1].Value.StartsWith("data:") || link.Groups[1].Value.StartsWith(Base + "/")).ShouldBeTrue(link.Value);
        html.ShouldContain("@media print");
        html.ShouldNotContain("картинки — у первых");
    }

    [Test]
    public async Task ExportHtml_Over500_ImagesCappedWithNote()
    {
        var (admin, agent, partId, runId) = await RunAsync(Hits(520));
        await using var _ = agent;
        await ImagesAsync(partId);

        var html = await admin.GetStringAsync($"/api/clash/reports/{runId}/export.html?baseUrl={Base}");

        Regex.Matches(html, "data:image/png;base64,").Count.ShouldBe(ReportHtml.MaxImages);
        html.ShouldContain("картинки — у первых 500 из 520");
        Regex.Matches(html, "<tr class=\"row\"").Count.ShouldBe(20);
    }

    [Test]
    public async Task Export_Designer_Ok()
    {
        var (_, agent, _, runId) = await RunAsync(Hits(1));
        await using var __ = agent;
        var designer = await ClientAsync(UserRole.Designer, "ВК");

        (await designer.GetAsync($"/api/clash/reports/{runId}/export.xlsx?baseUrl={Base}")).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await designer.GetAsync($"/api/clash/reports/{runId}/export.html?baseUrl={Base}")).StatusCode.ShouldBe(HttpStatusCode.OK);
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
