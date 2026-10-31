using System.Net;
using Shouldly;
using ToadFlow.Contracts;
using ToadFlow.Server.Clash.Collisions;
using ToadFlow.Server.Clash.Parts;
using ToadFlow.Server.Clash.Reports;
using ToadFlow.Server.Tests.Infrastructure;
using static ToadFlow.Server.Tests.Infrastructure.ClashDraftFixtures;

namespace ToadFlow.Server.Tests;

public class ClashReportChatTests : ServerTest
{
    private const string Nbsp = " ";
    private static readonly TimeZoneInfo Moscow = TimeZoneInfo.CreateCustomTimeZone("test+3", TimeSpan.FromHours(3), "UTC+3", "UTC+3");
    private static readonly Guid RunId = Guid.Parse("0199a3f0-0000-7000-8000-000000000001");

    private static ReportView Report(ApprovedRunSummary summary, IReadOnlyList<ReportPairView> pairs, params (string Name, bool Unchecked)[] models) => new(
        new ReportRunView(RunId, Guid.NewGuid(), 2, new DateTimeOffset(2026, 10, 31, 9, 5, 0, TimeSpan.Zero), "schedule", "Ночное", 12.5),
        new CheckView(Guid.NewGuid(), "Часть АР-ОВ", "DON", "СОБ", 2, null, summary, 0, []),
        summary,
        models.Select(m => new ReportModelView(m.Name, "АР", DateTimeOffset.UnixEpoch, m.Unchecked)).ToList(),
        pairs,
        null,
        null);

    [Test]
    public void Compose_FullText_MatchesFormat()
    {
        var summary = new ApprovedRunSummary(2000, 1234, 300, 5, 7, 0, 3, 0, 0);
        var report = Report(summary, [new ReportPairView("ВК × АР", 20, 20, 0, 0, 0), new ReportPairView("ОВК × КР", 50, 50, 0, 0, 0)],
            ("DON_AR.rvt", true), ("DON_OV.rvt", false), ("DON_KR.rvt", true));

        var text = ReportChatMessage.Compose(report, "https://toadflow.local/clash/reports/x", Moscow);

        text.ShouldBe(
            "Коллизии: DON · Часть АР-ОВ (v2), прогон 31.10.2026 12:05\n" +
            $"Новых 1{Nbsp}234, вернулись 5, активных 300, исправлено 7\n" +
            "Новые по разделам: ОВК × КР — 50, ВК × АР — 20\n" +
            "Не проверено 3: не выгрузились DON_AR.rvt, DON_KR.rvt\n" +
            "Отчёт: https://toadflow.local/clash/reports/x");
    }

    [Test]
    public void Compose_NoNewNoUnchecked_LinesOmitted()
    {
        var report = Report(new ApprovedRunSummary(10, 0, 10, 0, 2, 0, 0, 0, 0), [new ReportPairView("ВК × АР", 10, 0, 0, 2, 0)], ("DON_AR.rvt", true));

        var text = ReportChatMessage.Compose(report, "https://x/r", Moscow);

        text.Split('\n').ShouldBe([
            "Коллизии: DON · Часть АР-ОВ (v2), прогон 31.10.2026 12:05",
            "Новых 0, вернулись 0, активных 10, исправлено 2",
            "Отчёт: https://x/r"
        ]);
    }

    [Test]
    public void Compose_TopFivePairs()
    {
        var pairs = Enumerable.Range(1, 7).Select(i => new ReportPairView($"П{i} × Р", i * 10, i * 10, 0, 0, 0)).ToList();
        var report = Report(new ApprovedRunSummary(280, 280, 0, 0, 0, 0, 0, 0, 0), pairs);

        var line = ReportChatMessage.Compose(report, "https://x/r", Moscow).Split('\n')[2];

        line.ShouldBe("Новые по разделам: П7 × Р — 70, П6 × Р — 60, П5 × Р — 50, П4 × Р — 40, П3 × Р — 30");
    }

    [Test]
    public void Compose_NoObject_NameOnly()
    {
        var full = Report(new ApprovedRunSummary(0, 0, 0, 0, 0, 0, 0, 0, 0), []);
        var report = full with { Check = full.Check with { Object = null } };

        ReportChatMessage.Compose(report, "https://x/r", Moscow).Split('\n')[0].ShouldBe("Коллизии: Часть АР-ОВ (v2), прогон 31.10.2026 12:05");
    }

    [Test]
    public async Task Chat_RelativeBaseUrl_400()
    {
        var admin = await AdminAsync();
        await using var agent = await FakeAgent.RegisterAsync(Server, admin);
        await agent.ConnectAsync("1.8.0", new InstalledRevitDto(2022, true));
        var partId = await PartAsync(Server, admin, agent, "POL_K1_AR.rvt", "POL_K1_OV.rvt");
        var engine = EngineClient(Server);
        var approve = await admin.PostJsonAsync($"/api/clash/parts/{partId}/approve", new { comment = (string?)null, run = true });
        var runId = (await approve.ReadJsonAsync<ApprovedVersionView>()).RunId.ShouldNotBeNull();
        await engine.PostAsync("/api/engine/runs/claim", null);
        (await PostApprovedResultAsync(engine, runId, 0, true, [Hit(1)], Stats, [], [])).EnsureSuccessStatusCode();

        (await admin.GetAsync($"/api/clash/reports/{runId}/chat?baseUrl=/relative")).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await admin.GetAsync($"/api/clash/reports/{runId}/chat?baseUrl=ftp://host")).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await admin.GetAsync($"/api/clash/reports/{runId}/chat")).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        var chat = await admin.GetJsonAsync<ChatMessageView>($"/api/clash/reports/{runId}/chat?baseUrl=http://toadflow.local/");
        chat.Text.ShouldEndWith($"Отчёт: http://toadflow.local/clash/reports/{runId}");
        chat.Text.ShouldContain("Новых 1,");
    }
}
