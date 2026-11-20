using System.Net;
using Alexpivo394.Revit.ParamCheck.Summaries;
using Microsoft.Extensions.Time.Testing;
using Shouldly;
using ToadFlow.Contracts;
using ToadFlow.Contracts.Clash;
using ToadFlow.Server.Naming;
using ToadFlow.Server.Overview;
using ToadFlow.Server.Tests.Infrastructure;
using static ToadFlow.Server.Tests.Infrastructure.ClashDraftFixtures;
using OverviewDto = ToadFlow.Server.Overview.OverviewView;

namespace ToadFlow.Server.Tests;

/// <summary>Плитки модулей на «Обзоре»: числа — из расчётов, которые модули уже делают.</summary>
public class OverviewTilesTests : ServerTest
{
    private readonly FakeTimeProvider _time = new(DateTimeOffset.UtcNow);

    public OverviewTilesTests()
    {
        Time = _time;
        Services = OverviewFakes.Quiet;
    }

    private async Task<OverviewTile> TileAsync(string module) =>
        (await (await AdminAsync()).GetJsonAsync<OverviewDto>("/api/overview")).Tiles.Single(t => t.Module == module);

    [Test]
    public async Task ExportTile_CountsLastDay()
    {
        var now = _time.GetUtcNow();
        var next = now.AddHours(5);
        await DbAsync(async db =>
        {
            var run = Seed.Run(db, RunState.Completed);
            void Done(string kind, JobStatus status, DateTimeOffset finished)
            {
                var job = Seed.Job(db, run.Id);
                job.ModuleKind = kind;
                job.Status = status;
                job.FinishedAt = finished;
            }

            Done(ModuleKinds.Export, JobStatus.Succeeded, now.AddHours(-1));
            Done("", JobStatus.Succeeded, now.AddHours(-2));
            Done(ModuleKinds.Export, JobStatus.Warning, now.AddHours(-3));
            Done(ModuleKinds.Export, JobStatus.Failed, now.AddHours(-4));
            Done(ModuleKinds.Export, JobStatus.Succeeded, now.AddDays(-2)); // раньше суток
            Done(ModuleKinds.Loi, JobStatus.Succeeded, now.AddHours(-1)); // чужой модуль
            var schedule = Seed.Schedule(db, Seed.Source(db).Id);
            schedule.NextRunAt = next;
            await db.SaveChangesAsync();
        });

        var tile = await TileAsync("export");

        var local = TimeZoneInfo.ConvertTime(next, TimeZoneInfo.FindSystemTimeZoneById("Europe/Moscow"));
        (tile.Value, tile.Label, tile.Link).ShouldBe(("3", "за сутки", "/export/schedules"));
        tile.Lines.ShouldBe(["ошибок: 1", $"следующий запуск {local:dd.MM HH:mm}"]);
    }

    [Test]
    public async Task ExportTile_NoErrorsNoSchedules()
    {
        await DbAsync(async db =>
        {
            var job = Seed.Job(db, Seed.Run(db, RunState.Completed).Id);
            job.Status = JobStatus.Succeeded;
            job.FinishedAt = _time.GetUtcNow().AddHours(-1);
            await db.SaveChangesAsync();
        });

        (await TileAsync("export")).Lines.ShouldBe(["без ошибок", "расписаний нет"]);
    }

    [Test]
    public async Task ExportTile_EmptyBase()
    {
        var tile = await TileAsync("export");

        (tile.Value, tile.Label, tile.Link).ShouldBe(("—", "данных пока нет", "/export/schedules"));
    }

    private static CheckSummary Check(string model, int filled, DateTimeOffset at) => new()
    {
        ModelName = model, ModelPath = $"RSN://rs22/POL/{model}.rvt", ProfileName = "АР", ProfileFingerprint = "abc", CheckedAt = at,
        Elements = 2, CompleteElements = 1, Values = 4, Filled = filled, Empty = 4 - filled
    };

    [Test]
    public async Task LoiTile_FillRateTrendObjects()
    {
        var admin = await AdminAsync();
        var naming = NamingDefaults.Create() with { Entries = [..NamingDefaults.Create().Entries, new(NamingFields.Object, "POL", "Полковая")] };
        (await admin.PutJsonAsync("/api/naming", naming)).EnsureSuccessStatusCode();
        var now = _time.GetUtcNow();
        foreach (var check in new[] { Check("POL_AR", 2, now.AddDays(-10)), Check("POL_AR", 3, now.AddHours(-1)), Check("POL_KR", 4, now.AddDays(-20)) })
            (await admin.PostJsonAsync("/api/loi/checks", new { id = Guid.NewGuid(), summary = check })).EnsureSuccessStatusCode();

        var tile = await TileAsync("loi");

        // Сейчас: 3 + 4 из 8 = 88 %; неделю назад: 2 + 4 из 8 = 75 %.
        (tile.Value, tile.Label, tile.Trend, tile.Link).ShouldBe(("88 %", "заполнено", "▲ 13", "/loi/analytics"));
        tile.Lines.ShouldBe(["объектов: 1"]);
    }

    [Test]
    public async Task LoiTile_EmptyBase()
    {
        var tile = await TileAsync("loi");

        (tile.Value, tile.Label, tile.Trend).ShouldBe(("—", "данных пока нет", null));
    }

    [Test]
    public async Task ClashTile_EmptyBase()
    {
        var tile = await TileAsync("clash");

        (tile.Value, tile.Label, tile.Link).ShouldBe(("—", "данных пока нет", "/clash/summary"));
    }
}

public class OverviewClashTileTests : ServerTest
{
    private readonly FakeTimeProvider _time = new(DateTimeOffset.UtcNow);

    public OverviewClashTileTests()
    {
        Time = _time;
        Services = OverviewFakes.Quiet;
    }

    [Test]
    public async Task ClashTile_OpenAndWeek()
    {
        var admin = await AdminAsync();
        await using var agent = await FakeAgent.RegisterAsync(Server, admin);
        await agent.ConnectAsync("1.8.0", new InstalledRevitDto(2022, true));
        var partId = await PartAsync(Server, admin, agent, "POL_K1_AR.rvt", "POL_K1_OV.rvt");
        var engine = EngineClient(Server);
        var start = await admin.PostJsonAsync($"/api/clash/parts/{partId}/approve", new { comment = (string?)null, run = true });
        start.StatusCode.ShouldBe(HttpStatusCode.OK, await start.Content.ReadAsStringAsync());
        var runId = (await (await engine.PostAsync("/api/engine/runs/claim", null)).ReadJsonAsync<EngineRunAssignment>()).RunId;
        (await PostApprovedResultAsync(engine, runId, 0, true, new[] { Hit(1), Hit(2), Hit(3) }.Select(h => h with { FingerprintA = 1, FingerprintB = 2 }).ToList(),
            Stats, [], [])).EnsureSuccessStatusCode();

        var tile = (await admin.GetJsonAsync<OverviewDto>("/api/overview")).Tiles.Single(t => t.Module == "clash");

        (tile.Value, tile.Label, tile.Link).ShouldBe(("3", "открыто", "/clash/summary"));
        tile.Lines.ShouldBe(["новых за неделю: 3", "закрыто за неделю: 0"]);
    }
}

public class EngineWorkerProbeTests : ServerTest
{
    [Test]
    public async Task EngineProbe_ReportsLastSeen()
    {
        var admin = await AdminAsync();
        var before = (await admin.GetJsonAsync<ServerStats>("/api/server")).Workers.ShouldHaveSingleItem();
        (before.Module, before.Title, before.LastSeenAt, before.Online).ShouldBe(("clash", "Движок коллизий", null, true));

        (await EngineClient(Server).PostAsync("/api/engine/runs/claim", null)).StatusCode.ShouldBe(HttpStatusCode.NoContent);

        var after = (await admin.GetJsonAsync<ServerStats>("/api/server")).Workers.ShouldHaveSingleItem();
        after.LastSeenAt.ShouldNotBeNull();
        (await admin.GetJsonAsync<ServerStats>("/api/server")).Volumes.Select(v => v.Name).ShouldContain("Слепки коллизий");
    }
}
