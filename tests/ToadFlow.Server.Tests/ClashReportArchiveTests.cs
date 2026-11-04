using System.IO.Compression;
using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using Shouldly;
using ToadFlow.Contracts;
using ToadFlow.Contracts.Clash;
using ToadFlow.Server.Clash.Parts;
using ToadFlow.Server.Clash.Reports;
using ToadFlow.Server.Tests.Infrastructure;
using static ToadFlow.Server.Tests.Infrastructure.ClashDraftFixtures;

namespace ToadFlow.Server.Tests;

public class ClashReportArchiveTests : ServerTest
{
    private const string Base = "http://toadflow.local";
    private readonly FakeTimeProvider _time = new(DateTimeOffset.UtcNow);

    public ClashReportArchiveTests() => Time = _time;

    /// <summary>Пары АР × ОВ (1), АР × ВК (2) и ОВ × ВК (3).</summary>
    private static IReadOnlyList<EngineHit> ThreePairs(string vk = "ВК") =>
    [
        Hit(1, disciplineA: "АР", disciplineB: "ОВ"), Hit(2, disciplineA: "АР", disciplineB: vk), Hit(3, disciplineA: "АР", disciplineB: vk),
        Hit(4, disciplineA: vk, disciplineB: "ОВ"), Hit(5, disciplineA: vk, disciplineB: "ОВ"), Hit(6, disciplineA: vk, disciplineB: "ОВ")
    ];

    private async Task<(HttpClient Admin, FakeAgent Agent, Guid RunId)> RunAsync(IReadOnlyList<EngineHit> hits)
    {
        var admin = await AdminAsync();
        var agent = await FakeAgent.RegisterAsync(Server, admin);
        await agent.ConnectAsync("1.8.0", new InstalledRevitDto(2022, true));
        var partId = await PartAsync(Server, admin, agent, "POL_K1_AR.rvt", "POL_K1_OV.rvt");
        var engine = EngineClient(Server);
        var approve = await admin.PostJsonAsync($"/api/clash/parts/{partId}/approve", new { comment = (string?)null, run = true });
        var runId = (await approve.ReadJsonAsync<ApprovedVersionView>()).RunId.ShouldNotBeNull();
        (await engine.PostAsync("/api/engine/runs/claim", null)).EnsureSuccessStatusCode();
        (await PostApprovedResultAsync(engine, runId, 0, true, hits, Stats, [], [])).EnsureSuccessStatusCode();
        return (admin, agent, runId);
    }

    private static async Task<ReportArchiveView> StartAsync(HttpClient client, Guid runId, string query = "")
    {
        var response = await client.PostAsync($"/api/clash/reports/{runId}/archive?scope=changes&baseUrl={Base}{query}", null);
        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        return await response.ReadJsonAsync<ReportArchiveView>();
    }

    private static async Task<ReportArchiveView> ReadyAsync(HttpClient client)
    {
        ReportArchiveView? view = null;
        await EventuallyAsync(async () =>
        {
            view = await client.GetJsonAsync<ReportArchiveView>("/api/clash/report-archives/current");
            return view.State is ReportArchives.Ready or ReportArchives.Failed;
        });
        view!.State.ShouldBe(ReportArchives.Ready, view.Error);
        return view;
    }

    private static async Task<ZipArchive> DownloadAsync(HttpClient client, Guid id)
    {
        var response = await client.GetAsync($"/api/clash/report-archives/{id}/download");
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        response.Content.Headers.ContentType!.MediaType.ShouldBe("application/zip");
        return new ZipArchive(new MemoryStream(await response.Content.ReadAsByteArrayAsync()), ZipArchiveMode.Read);
    }

    private static string Text(ZipArchive zip, string name)
    {
        using var reader = new StreamReader(zip.GetEntry(name).ShouldNotBeNull(name).Open());
        return reader.ReadToEnd();
    }

    [Test]
    public async Task Archive_All_OneFilePerPairInOrderPlusIndex()
    {
        var (admin, agent, runId) = await RunAsync(ThreePairs());
        await using var _ = agent;

        var started = await StartAsync(admin, runId);
        var ready = await ReadyAsync(admin);

        ready.Id.ShouldBe(started.Id);
        (ready.PairsDone, ready.PairsTotal).ShouldBe((3, 3));
        ready.SizeBytes.ShouldNotBeNull().ShouldBeGreaterThan(0);
        ready.ExpiresAt.ShouldNotBeNull().ShouldBe(_time.GetUtcNow() + ReportArchives.Lifetime, TimeSpan.FromSeconds(5));
        using var zip = await DownloadAsync(admin, ready.Id);
        zip.Entries.Select(e => e.FullName).ShouldBe(["01 АР × ОВ.html", "02 АР × ВК.html", "03 ОВ × ВК.html", "index.html"]);
        Text(zip, "03 ОВ × ВК.html").ShouldContain("Коллизии ОВ × ВК");
        var index = Text(zip, "index.html");
        index.ShouldContain("href=\"01 АР × ОВ.html\"");
        index.ShouldContain("href=\"03 ОВ × ВК.html\"");
        index.ShouldContain("<td class=\"num\">3</td>");
    }

    [Test]
    public async Task Archive_Discipline_OnlyItsPairs()
    {
        var (admin, agent, runId) = await RunAsync(ThreePairs());
        await using var _ = agent;

        await StartAsync(admin, runId, "&discipline=ОВ");
        var ready = await ReadyAsync(admin);

        ready.Discipline.ShouldBe("ОВ");
        using var zip = await DownloadAsync(admin, ready.Id);
        zip.Entries.Select(e => e.FullName).ShouldBe(["01 АР × ОВ.html", "02 ОВ × ВК.html", "index.html"]);
    }

    [Test]
    public async Task Archive_PairParam_400()
    {
        var (admin, agent, runId) = await RunAsync(ThreePairs());
        await using var _ = agent;

        (await admin.PostAsync($"/api/clash/reports/{runId}/archive?baseUrl={Base}&pair={Uri.EscapeDataString("АР × ОВ")}", null)).StatusCode
            .ShouldBe(HttpStatusCode.BadRequest);
        (await admin.PostAsync($"/api/clash/reports/{runId}/archive", null)).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await admin.PostAsync($"/api/clash/reports/{Guid.NewGuid()}/archive?baseUrl={Base}", null)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Test]
    public void FileName_WindowsInvalidChars_ReplacedOnAnyOs()
    {
        ReportPairFile.FileName(["ВК", "a<b>:c\"d/e\\f|g?h*i\tj", null, " "]).ShouldBe("ВК a_b__c_d_e_f_g_h_i_j");
    }

    [Test]
    public async Task Archive_FileNames_Sanitized()
    {
        var (admin, agent, runId) = await RunAsync(ThreePairs(vk: "ВК/<1>"));
        await using var _ = agent;

        await StartAsync(admin, runId);
        using var zip = await DownloadAsync(admin, (await ReadyAsync(admin)).Id);

        zip.Entries.Select(e => e.FullName).ShouldBe(["01 АР × ОВ.html", "02 АР × ВК__1_.html", "03 ОВ × ВК__1_.html", "index.html"]);
        Text(zip, "02 АР × ВК__1_.html").ShouldContain("АР × ВК/&lt;1&gt;");
        Text(zip, "index.html").ShouldNotContain("<1>");
    }

    [Test]
    public async Task Archive_SecondStart_ReplacesFirst()
    {
        var (admin, agent, runId) = await RunAsync(ThreePairs());
        await using var _ = agent;

        var first = await StartAsync(admin, runId);
        var second = await StartAsync(admin, runId, "&discipline=АР");
        var ready = await ReadyAsync(admin);

        ready.Id.ShouldBe(second.Id);
        (await admin.GetAsync($"/api/clash/report-archives/{first.Id}/download")).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        var exports = Path.Combine(Server.Services.GetRequiredService<Microsoft.Extensions.Options.IOptions<Clash.ClashStorageOptions>>().Value.Root, "exports");
        await EventuallyAsync(() => Task.FromResult(Directory.GetFiles(exports).Select(Path.GetFileName).SequenceEqual([$"{second.Id:N}.zip"])));
    }

    [Test]
    public async Task Archive_Cancel_StateCancelled()
    {
        var (admin, agent, runId) = await RunAsync(ThreePairs());
        await using var _ = agent;
        var started = await StartAsync(admin, runId);

        (await admin.DeleteAsync($"/api/clash/report-archives/{started.Id}")).StatusCode.ShouldBe(HttpStatusCode.NoContent);

        (await admin.GetAsync("/api/clash/report-archives/current")).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await admin.GetAsync($"/api/clash/report-archives/{started.Id}/download")).StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Test]
    public async Task Archive_OtherUser_404()
    {
        var (admin, agent, runId) = await RunAsync(ThreePairs());
        await using var _ = agent;
        await StartAsync(admin, runId);
        var mine = await ReadyAsync(admin);
        var other = await ClientAsync(UserRole.Operator);

        (await other.GetAsync($"/api/clash/report-archives/{mine.Id}/download")).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await other.DeleteAsync($"/api/clash/report-archives/{mine.Id}")).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await other.GetAsync("/api/clash/report-archives/current")).StatusCode.ShouldBe(HttpStatusCode.NoContent);
    }

    [Test]
    public async Task Archive_Expired_410()
    {
        var (admin, agent, runId) = await RunAsync(ThreePairs());
        await using var _ = agent;
        await StartAsync(admin, runId);
        var ready = await ReadyAsync(admin);

        _time.Advance(ReportArchives.Lifetime + TimeSpan.FromMinutes(1));

        (await admin.GetAsync($"/api/clash/report-archives/{ready.Id}/download")).StatusCode.ShouldBe(HttpStatusCode.Gone);
        Server.Services.GetRequiredService<ReportArchives>().Clean();
        (await admin.GetJsonAsync<ReportArchiveView>("/api/clash/report-archives/current")).State.ShouldBe(ReportArchives.Expired);
        var exports = Path.Combine(Server.Services.GetRequiredService<Microsoft.Extensions.Options.IOptions<Clash.ClashStorageOptions>>().Value.Root, "exports");
        Directory.GetFiles(exports).ShouldNotContain(f => f.Contains(ready.Id.ToString("N")));
    }

    [Test]
    public async Task Archive_Designer_Ok()
    {
        var (_, agent, runId) = await RunAsync(ThreePairs());
        await using var __ = agent;
        var designer = await ClientAsync(UserRole.Designer, "ОВ");

        await StartAsync(designer, runId, "&discipline=ОВ");
        using var zip = await DownloadAsync(designer, (await ReadyAsync(designer)).Id);

        zip.Entries.Count.ShouldBe(3);
    }
}
