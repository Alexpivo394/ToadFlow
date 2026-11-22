using Alexpivo394.Revit.ParamCheck.Summaries;
using Microsoft.Extensions.Time.Testing;
using Shouldly;
using ToadFlow.Contracts;
using ToadFlow.Server.Families;
using ToadFlow.Server.Families.Data;
using ToadFlow.Server.Overview;
using ToadFlow.Server.Tests.Infrastructure;
using OverviewDto = ToadFlow.Server.Overview.OverviewView;

namespace ToadFlow.Server.Tests;

/// <summary>Ряд за 14 дней и фраза для шапки у плиток модулей. Время — полдень по Москве (пояс по умолчанию).</summary>
public class OverviewSeriesTests : ServerTest
{
    private static readonly DateTimeOffset Now = new(2026, 11, 21, 9, 0, 0, TimeSpan.Zero);
    private readonly FakeTimeProvider _time = new(Now);

    public OverviewSeriesTests()
    {
        Time = _time;
        Services = services =>
        {
            OverviewFakes.Quiet(services);
            FamiliesFixtures.WithoutScanLoop(services);
            OverviewFakes.TokensOnTestClock(services);
        };
    }

    private async Task<OverviewTile> TileAsync(string module) =>
        (await (await AdminAsync()).GetJsonAsync<OverviewDto>("/api/overview")).Tiles.Single(t => t.Module == module);

    private Task DoneAsync(string kind, JobStatus status, params DateTimeOffset[] finished) => DbAsync(async db =>
    {
        var run = Seed.Run(db, RunState.Completed);
        foreach (var at in finished)
        {
            var job = Seed.Job(db, run.Id);
            job.ModuleKind = kind;
            job.Status = status;
            job.FinishedAt = at;
        }

        await db.SaveChangesAsync();
    });

    [Test]
    public async Task ExportTile_SeriesAndHighlight()
    {
        await DoneAsync(ModuleKinds.Export, JobStatus.Succeeded, Now.AddHours(-1), Now.AddHours(-2), Now.AddDays(-1));
        await DoneAsync(ModuleKinds.Export, JobStatus.Failed, Now.AddHours(-1));

        var tile = await TileAsync("export");

        tile.Series!.Count.ShouldBe(14);
        (tile.Series[13], tile.Series[12], tile.Series[0]).ShouldBe((2, 1, 0));
        tile.Highlight.ShouldBe("выгружено 3 модели"); // за 24 часа: два сегодня и одно ровно сутки назад
    }

    [Test]
    public async Task ExportTile_NothingDone_NoHighlight()
    {
        await DoneAsync(ModuleKinds.Export, JobStatus.Succeeded, Now.AddDays(-3));

        var tile = await TileAsync("export");

        tile.Highlight.ShouldBeNull();
        tile.Series![10].ShouldBe(1);
    }

    private static CheckSummary Check(string model, int filled, DateTimeOffset at) => new()
    {
        ModelName = model, ModelPath = $"RSN://rs22/POL/{model}.rvt", ProfileName = "АР", ProfileFingerprint = "abc", CheckedAt = at,
        Elements = 2, CompleteElements = 1, Values = 4, Filled = filled, Empty = 4 - filled
    };

    [Test]
    public async Task LoiTile_SeriesCarriesLastValue()
    {
        var admin = await AdminAsync();
        foreach (var check in new[] { Check("POL_AR", 2, Now.AddDays(-3)), Check("POL_AR", 3, Now.AddHours(-1)) })
            (await admin.PostJsonAsync("/api/loi/checks", new { id = Guid.NewGuid(), summary = check })).EnsureSuccessStatusCode();
        await DoneAsync(ModuleKinds.Loi, JobStatus.Succeeded, Now.AddHours(-1));

        var tile = await TileAsync("loi");

        (tile.Series![0], tile.Series[10], tile.Series[11], tile.Series[12], tile.Series[13]).ShouldBe((0, 50, 50, 50, 75));
        tile.Highlight.ShouldBe("проверена 1 модель");
    }

    [Test]
    public async Task FamiliesTile_SeriesOfCatalog()
    {
        var admin = await AdminAsync();
        (await admin.PutJsonAsync("/api/families/settings", new FamiliesSettings(FamiliesFixtures.Root))).EnsureSuccessStatusCode();
        await DbAsync(db => FamiliesFixtures.SeedCardAsync(db, "Клапан", @"ОВ\Клапан.rfa", CardState.Approved, null, Now.AddDays(-2)));
        await DbAsync(db => FamiliesFixtures.SeedCardAsync(db, "Кран", @"ОВ\Кран.rfa", CardState.Approved, null, Now));

        var tile = await TileAsync("families");

        (tile.Series!.Count, tile.Series[10], tile.Series[11], tile.Series[13]).ShouldBe((14, 0, 1, 2));
    }

    [Test]
    public async Task ClashTile_EmptyBase_NoSeries()
    {
        var tile = await TileAsync("clash");

        (tile.Series, tile.Highlight).ShouldBe((null, null));
    }
}
