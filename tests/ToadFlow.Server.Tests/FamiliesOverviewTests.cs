using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Time.Testing;
using Shouldly;
using ToadFlow.Contracts;
using ToadFlow.Server.Families;
using ToadFlow.Server.Families.Data;
using ToadFlow.Server.Families.Requests;
using ToadFlow.Server.Overview;
using ToadFlow.Server.Tests.Infrastructure;
using OverviewDto = ToadFlow.Server.Overview.OverviewView;

namespace ToadFlow.Server.Tests;

public class FamiliesOverviewTests : ServerTest
{
    private readonly FakeTimeProvider _time = new(DateTimeOffset.UtcNow);

    public FamiliesOverviewTests()
    {
        Time = _time;
        Services = services =>
        {
            OverviewFakes.Quiet(services);
            FamiliesFixtures.WithoutScanLoop(services);
        };
    }

    private FakeAgent? _agent;

    [After(Test)]
    public async Task StopAgentAsync()
    {
        if (_agent is not null) await _agent.DisposeAsync();
    }

    private async Task ConfigureAsync(bool withAgent)
    {
        var admin = await AdminAsync();
        (await admin.PutJsonAsync("/api/families/settings", new FamiliesSettings(FamiliesFixtures.Root))).EnsureSuccessStatusCode();
        if (!withAgent) return;
        _agent = await FakeAgent.RegisterAsync(Server, admin, "BIM-LIB-01");
        await _agent.ConnectAsync("2.5.0", FamiliesFixtures.AgentModules, new InstalledRevitDto(2022, true));
    }

    private Task ScanAsync(int minutesAgo, bool complete) => DbAsync(async db =>
    {
        var at = _time.GetUtcNow().AddMinutes(-minutesAgo);
        db.Set<FamilyScan>().Add(new FamilyScan { Id = Guid.CreateVersion7(), StartedAt = at.AddMinutes(-1), FinishedAt = at, Complete = complete });
        await db.SaveChangesAsync();
    });

    private Task IssueAsync(IssueKind kind) => DbAsync(async db =>
    {
        db.Set<FamilyIssue>().Add(new FamilyIssue { Id = Guid.CreateVersion7(), Kind = kind, RelativePath = @"ОВ\Клапан.rfa", Text = "…", CreatedAt = _time.GetUtcNow() });
        await db.SaveChangesAsync();
    });

    private async Task<OverviewDto> OverviewAsync(UserRole? role = null) =>
        await (role is { } r ? await ClientAsync(r) : await AdminAsync()).GetJsonAsync<OverviewDto>("/api/overview");

    private static string[] FamilyLines(OverviewDto overview) => overview.Attention.Where(a => a.Module == "families").Select(a => a.Text).ToArray();

    [Test]
    public async Task Tile_NotConfigured()
    {
        var tile = (await OverviewAsync()).Tiles.Single(t => t.Module == "families");

        (tile.Value, tile.Label, tile.Link).ShouldBe(("—", "папка не задана", "/families/settings"));
        FamilyLines(await OverviewAsync()).ShouldBeEmpty();
    }

    [Test]
    public async Task Tile_CountsApproved()
    {
        await ConfigureAsync(withAgent: true);
        var now = _time.GetUtcNow();
        await DbAsync(db => FamiliesFixtures.SeedCardAsync(db, "Клапан", @"ОВ\Клапан.rfa", CardState.Approved, null, now));
        await DbAsync(db => FamiliesFixtures.SeedCardAsync(db, "Кран", @"ОВ\Кран.rfa", CardState.Approved, null, now));
        await DbAsync(db => FamiliesFixtures.SeedCardAsync(db, "Новый", @"_На проверке\Новый.rfa", CardState.InReview, null, now));
        await DbAsync(db => FamiliesFixtures.SeedCardAsync(db, "Старый", @"ОВ\Старый.rfa", CardState.Archived, null, now));
        await IssueAsync(IssueKind.NameConflict);

        var tile = (await OverviewAsync()).Tiles.Single(t => t.Module == "families");

        (tile.Value, tile.Label, tile.Link).ShouldBe(("2", "в каталоге", "/families/catalog"));
        tile.Lines.ShouldBe(["заявок ждёт: 0", "на проверке: 1", "нужно решение: 1"]);
    }

    [Test]
    public async Task Attention_LibraryUnreachable_AfterThreeIntervals()
    {
        await ConfigureAsync(withAgent: true);
        await ScanAsync(minutesAgo: 25, complete: true);
        await ScanAsync(minutesAgo: 5, complete: false);
        FamilyLines(await OverviewAsync()).ShouldBeEmpty();

        // Полный обход был 35 минут назад, интервал 10
        await DbAsync(db => db.Set<FamilyScan>().Where(s => s.Complete == true)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.FinishedAt, _time.GetUtcNow().AddMinutes(-35))));

        var line = FamilyLines(await OverviewAsync()).ShouldHaveSingleItem();
        line.ShouldStartWith("Семейства: библиотека недоступна — последний полный обход");
        (await OverviewAsync()).Attention.Single(a => a.Module == "families").Link.ShouldBe("/families/settings");
    }

    [Test]
    public async Task Attention_NeverScannedComplete_AfterThreeIntervals()
    {
        await ConfigureAsync(withAgent: true);
        await ScanAsync(minutesAgo: 40, complete: false);

        FamilyLines(await OverviewAsync()).ShouldBe(["Семейства: библиотека недоступна — полного обхода ещё не было"]);
    }

    [Test]
    public async Task Attention_NoAgent()
    {
        await ConfigureAsync(withAgent: false);

        var item = (await OverviewAsync()).Attention.Single(a => a.Module == "families");

        (item.Level, item.Text, item.Link).ShouldBe((AttentionLevel.Problem, "Семейства: нет агента для обхода библиотеки", "/families/settings"));
    }

    [Test]
    public async Task Attention_Issues_HiddenFromViewer()
    {
        await ConfigureAsync(withAgent: true);
        await ScanAsync(minutesAgo: 1, complete: true);
        await IssueAsync(IssueKind.NameConflict);
        await IssueAsync(IssueKind.Duplicate);
        await IssueAsync(IssueKind.Unreadable);
        await DbAsync(async db =>
        {
            var id = await FamiliesFixtures.SeedCardAsync(db, "Битый", @"ОВ\Битый.rfa", CardState.Approved, null, _time.GetUtcNow());
            var version = db.Set<FamilyVersion>().Single(v => v.CardId == id);
            version.ReadState = ReadState.Failed;
            await db.SaveChangesAsync();
        });

        var admin = await OverviewAsync();
        var viewer = await OverviewAsync(UserRole.Viewer);

        admin.Attention.Where(a => a.Module == "families").Select(a => (a.Level, a.Text, a.Link)).ShouldBe([
            (AttentionLevel.Todo, "Семейства: 3 случая ждут решения", "/families/issues"),
            (AttentionLevel.Problem, "Семейства: 1 файл не удалось прочитать", "/families/issues")
        ], ignoreOrder: true);
        FamilyLines(viewer).ShouldBeEmpty();
    }

    [Test]
    public async Task Requests_WaitingOnTile_NewOnesInAttentionForCoordinator()
    {
        await ConfigureAsync(withAgent: true);
        await ScanAsync(minutesAgo: 1, complete: true);
        var author = await DbAsync(db => db.Users.Select(u => u.Id).FirstAsync());
        await DbAsync(async db =>
        {
            var now = _time.GetUtcNow();
            foreach (var state in new[] { RequestState.New, RequestState.InWork, RequestState.Done })
                db.Set<FamilyRequest>().Add(new FamilyRequest
                {
                    Id = Guid.CreateVersion7(), Title = state.ToString(), State = state, AuthorUserId = author, CreatedAt = now, UpdatedAt = now
                });
            await db.SaveChangesAsync();
        });

        var admin = await OverviewAsync();

        admin.Tiles.Single(t => t.Module == "families").Lines[0].ShouldBe("заявок ждёт: 2");
        FamilyLines(admin).ShouldContain("Семейства: 1 новая заявка ждёт исполнителя");
        FamilyLines(await OverviewAsync(UserRole.Viewer)).ShouldBeEmpty();
    }
}
