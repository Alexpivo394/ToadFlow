using System.Net;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using ToadFlow.Contracts;
using ToadFlow.Contracts.Clash;
using ToadFlow.Server.Clash;
using ToadFlow.Server.Clash.Collisions;
using ToadFlow.Server.Clash.Engine;
using ToadFlow.Server.Clash.Parts;
using ToadFlow.Server.Clash.Reports;
using ToadFlow.Server.Data.Entities;
using ToadFlow.Server.Events;
using ToadFlow.Server.Tests.Infrastructure;
using static ToadFlow.Server.Tests.Infrastructure.ClashDraftFixtures;

namespace ToadFlow.Server.Tests;

public class ClashReportTests : ServerTest
{
    private async Task<(HttpClient Admin, FakeAgent Agent, Guid PartId, HttpClient Engine)> SetupAsync()
    {
        var admin = await AdminAsync();
        var agent = await FakeAgent.RegisterAsync(Server, admin);
        await agent.ConnectAsync("1.8.0", new InstalledRevitDto(2022, true));
        var partId = await PartAsync(Server, admin, agent, "POL_K1_AR.rvt", "POL_K1_OV.rvt");
        return (admin, agent, partId, EngineClient(Server));
    }

    /// <summary>Утверждённый прогон (первый — с утверждением), сданный работником одной частью.</summary>
    private async Task<Guid> RunAsync(HttpClient admin, HttpClient engine, Guid partId, IReadOnlyList<EngineHit> hits, bool approve = false,
        IReadOnlyCollection<Guid>? unchecked_ = null)
    {
        Guid runId;
        if (approve)
        {
            var response = await admin.PostJsonAsync($"/api/clash/parts/{partId}/approve", new { comment = (string?)null, run = true });
            response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
            runId = (await response.ReadJsonAsync<ApprovedVersionView>()).RunId.ShouldNotBeNull();
        }
        else
        {
            using var scope = Server.Services.CreateScope();
            runId = (await scope.ServiceProvider.GetRequiredService<ApprovedRuns>().EnqueueAsync(partId, null, unchecked_ ?? [], Actor.System, CancellationToken.None))
                .ShouldNotBeNull().Id;
        }

        (await (await engine.PostAsync("/api/engine/runs/claim", null)).ReadJsonAsync<EngineRunAssignment>()).RunId.ShouldBe(runId);
        var result = await PostApprovedResultAsync(engine, runId, 0, true, hits, Stats, [], []);
        result.StatusCode.ShouldBe(HttpStatusCode.OK, await result.Content.ReadAsStringAsync());
        return runId;
    }

    private static EngineHit Fp(EngineHit hit) => hit with { FingerprintA = 11, FingerprintB = 22 };

    private static Task<ReportPage> RowsAsync(HttpClient client, Guid runId, string query = "scope=changes") =>
        client.GetJsonAsync<ReportPage>($"/api/clash/reports/{runId}/collisions?{query}");

    private Task<Guid> ModelIdAsync(Guid partId, string name) => DbAsync(db =>
        db.Set<ClashPartModel>().Where(m => m.PartId == partId).Join(db.Set<ClashModel>(), pm => pm.ModelId, m => m.Id, (_, m) => m)
            .Where(m => m.Name == name).Select(m => m.Id).SingleAsync());

    private static async Task SetStatusAsync(HttpClient admin, Guid partId, int number, CollisionStatus status, string? note = null)
    {
        var id = (await admin.GetJsonAsync<CollisionPage>($"/api/clash/parts/{partId}/approved/collisions?status=all&q={number}")).Items.Single().Id;
        (await admin.PostJsonAsync($"/api/clash/collisions/{id}/status", new { status, note })).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Test]
    public async Task Report_View_HeaderSummaryModelsPairs()
    {
        var (admin, agent, partId, engine) = await SetupAsync();
        await using var _ = agent;
        await RunAsync(admin, engine, partId, [Fp(Hit(1)), Fp(Hit(2)), Fp(Hit(3, disciplineA: "ВК", disciplineB: "КР"))], approve: true);
        await SetStatusAsync(admin, partId, 3, CollisionStatus.Fake, "так задумано");
        var ar = await ModelIdAsync(partId, "POL_K1_AR.rvt");

        var second = await RunAsync(admin, engine, partId, [Fp(Hit(1)), Fp(Hit(4)), Fp(Hit(3, disciplineA: "ВК", disciplineB: "КР"))], unchecked_: [ar]);

        var report = await admin.GetJsonAsync<ReportView>($"/api/clash/reports/{second}");
        (report.Run.RunId, report.Run.PartId, report.Run.Version, report.Run.Trigger).ShouldBe((second, partId, 1, "manual"));
        report.Run.SearchSeconds.ShouldBe(Stats.BroadSeconds + Stats.NarrowSeconds, 1e-9);
        report.Check.PartId.ShouldBe(partId);
        var run = await DbAsync(db => db.Set<EngineRun>().AsNoTracking().SingleAsync(r => r.Id == second));
        report.Summary.ShouldBe(run.Summary);
        report.Models.Select(m => (m.Name, m.Unchecked)).OrderBy(m => m.Name).ShouldBe([("POL_K1_AR.rvt", true), ("POL_K1_OV.rvt", false)]);
        report.Models.ShouldAllBe(m => m.UploadedAt != null && m.Discipline.Length > 0);
        report.Pairs.Select(p => (p.Label, p.Open, p.New, p.Returned, p.Fixed, p.Fake)).ShouldBe([("АР × ОВ", 2, 1, 0, 1, 0), ("КР × ВК", 0, 0, 0, 0, 1)]);
        (report.NewerRunId, report.NewerRunAt).ShouldBe((null, null));
    }

    [Test]
    public async Task Report_PairsAndDisciplines_InNamingOrder()
    {
        var (admin, agent, partId, engine) = await SetupAsync();
        await using var _ = agent;
        var hits = Enumerable.Range(1, 5).Select(i => Fp(Hit(i, disciplineA: "ВК", disciplineB: "ОВ")))
            .Append(Fp(Hit(6, disciplineA: "АР", disciplineB: "ОВ")))
            .Concat(Enumerable.Range(7, 2).Select(i => Fp(Hit(i, disciplineA: "АР", disciplineB: "ВК"))))
            .ToList();
        var runId = await RunAsync(admin, engine, partId, hits, approve: true);

        var report = await admin.GetJsonAsync<ReportView>($"/api/clash/reports/{runId}");

        report.Pairs.Select(p => p.Label).ShouldBe(["АР × ОВ", "АР × ВК", "ОВ × ВК"]);
        report.Pairs.Select(p => (p.DisciplineA, p.DisciplineB)).ShouldBe([("АР", "ОВ"), ("АР", "ВК"), ("ОВ", "ВК")]);
        report.Disciplines.Select(d => d.Name).ShouldBe(["АР", "ОВ", "ВК"]);
        var ov = report.Disciplines.Single(d => d.Name == "ОВ");
        ov.Pairs.ShouldBe(["АР × ОВ", "ВК × ОВ"]);
        (ov.Open, ov.New).ShouldBe((6, 6));
    }

    [Test]
    public async Task Report_StartedBy_UserOrSchedule()
    {
        var (admin, agent, partId, engine) = await SetupAsync();
        await using var _ = agent;
        var first = await RunAsync(admin, engine, partId, [Fp(Hit(1))], approve: true);

        var report = await admin.GetJsonAsync<ReportView>($"/api/clash/reports/{first}");

        (report.Run.Trigger, report.Run.StartedBy).ShouldBe(("manual", "Администратор"));
    }

    [Test]
    public async Task Report_Changes_NewReturnedFixedOfThisRunOnly()
    {
        var (admin, agent, partId, engine) = await SetupAsync();
        await using var _ = agent;
        await RunAsync(admin, engine, partId, [Fp(Hit(1)), Fp(Hit(2))], approve: true);
        var second = await RunAsync(admin, engine, partId, [Fp(Hit(1))]);
        var third = await RunAsync(admin, engine, partId, [Fp(Hit(1)), Fp(Hit(2)), Fp(Hit(3))]);

        var changes = await RowsAsync(admin, third);
        changes.Items.Select(r => (r.Collision.Number, r.Change, r.Collision.Status))
            .ShouldBe([(2, (CollisionStatus?)CollisionStatus.Returned, CollisionStatus.Returned), (3, CollisionStatus.New, CollisionStatus.New)]);
        changes.Total.ShouldBe(2);
        // Новая → активная — не изменение: прогон 2 показывает только исправленную.
        (await RowsAsync(admin, second)).Items.Select(r => (r.Collision.Number, r.Change)).ShouldBe([(2, (CollisionStatus?)CollisionStatus.Fixed)]);
    }

    [Test]
    public async Task Report_OldRun_ChangesOfThatRunAndNewerLink()
    {
        var (admin, agent, partId, engine) = await SetupAsync();
        await using var _ = agent;
        var first = await RunAsync(admin, engine, partId, [Fp(Hit(1)), Fp(Hit(2))], approve: true);
        await RunAsync(admin, engine, partId, [Fp(Hit(1))]);
        var third = await RunAsync(admin, engine, partId, [Fp(Hit(1)), Fp(Hit(2)), Fp(Hit(3))]);

        var report = await admin.GetJsonAsync<ReportView>($"/api/clash/reports/{first}");
        var rows = await RowsAsync(admin, first);

        report.NewerRunId.ShouldBe(third);
        report.NewerRunAt.ShouldNotBeNull();
        rows.Items.Select(r => (r.Collision.Number, r.Change, r.Collision.Status))
            .ShouldBe([(1, (CollisionStatus?)CollisionStatus.New, CollisionStatus.Active), (2, CollisionStatus.New, CollisionStatus.Returned)]);
        (await admin.GetJsonAsync<ReportView>($"/api/clash/reports/{third}")).NewerRunId.ShouldBeNull();
    }

    [Test]
    public async Task Report_Open_CurrentOpenOfPart()
    {
        var (admin, agent, partId, engine) = await SetupAsync();
        await using var _ = agent;
        var first = await RunAsync(admin, engine, partId, [Fp(Hit(1)), Fp(Hit(2)), Fp(Hit(3))], approve: true);
        await RunAsync(admin, engine, partId, [Fp(Hit(1)), Fp(Hit(3))]);
        await SetStatusAsync(admin, partId, 3, CollisionStatus.Fake, "так задумано");

        var open = await RowsAsync(admin, first, "scope=open");

        open.Items.Select(r => (r.Collision.Number, r.Change)).ShouldBe([(1, (CollisionStatus?)CollisionStatus.New)]);
    }

    [Test]
    public async Task Report_Filters_DisciplinePairAssigneeLevelModel()
    {
        var (admin, agent, partId, engine) = await SetupAsync();
        await using var _ = agent;
        var run = await RunAsync(admin, engine, partId,
        [
            Fp(Hit(1)),
            Fp(Hit(2, disciplineA: "ВК", disciplineB: "КР")),
            Fp(Hit(3, modelB: "c") with { LevelB = "Этаж 2" }),
            Fp(Hit(4, disciplineA: "ВК"))
        ], approve: true);
        var id4 = (await RowsAsync(admin, run, "scope=open&q=4")).Items.Single().Collision.Id;
        (await admin.PostJsonAsync($"/api/clash/collisions/{id4}/assignee", new { assignee = "SideB" })).EnsureSuccessStatusCode();

        async Task<int[]> NumbersAsync(string filter) =>
            (await RowsAsync(admin, run, "scope=changes&" + filter)).Items.Select(r => r.Collision.Number).ToArray();

        (await NumbersAsync("discipline=ВК")).ShouldBe([2, 4]);
        (await NumbersAsync("pair=" + Uri.EscapeDataString("АР × ОВ"))).ShouldBe([1, 3]);
        (await NumbersAsync("assignee=SideB")).ShouldBe([4]);
        (await NumbersAsync("level=" + Uri.EscapeDataString("этаж 2"))).ShouldBe([3]);
        (await NumbersAsync("model=c")).ShouldBe([3]);
        (await NumbersAsync("status=New&discipline=ОВ")).ShouldBe([1, 3, 4]);
        (await NumbersAsync("sort=depth&q=ub-2")).ShouldBe([2]);
    }

    [Test]
    public async Task Report_DraftOrFailedRun_404()
    {
        var (admin, agent, partId, _) = await SetupAsync();
        await using var __ = agent;
        var draft = await (await admin.PostAsync($"/api/clash/parts/{partId}/draft/run", null)).ReadJsonAsync<DraftRunSummary>();

        (await admin.GetAsync($"/api/clash/reports/{draft.RunId}")).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await admin.GetAsync($"/api/clash/reports/{draft.RunId}/collisions")).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await admin.GetAsync($"/api/clash/reports/{Guid.NewGuid()}")).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        var queued = await admin.PostJsonAsync($"/api/clash/parts/{partId}/approve", new { comment = (string?)null, run = true });
        var queuedRun = (await queued.ReadJsonAsync<ApprovedVersionView>()).RunId.ShouldNotBeNull();
        (await admin.GetAsync($"/api/clash/reports/{queuedRun}")).StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Test]
    public async Task Report_Designer_CanRead()
    {
        var (admin, agent, partId, engine) = await SetupAsync();
        await using var _ = agent;
        var run = await RunAsync(admin, engine, partId, [Fp(Hit(1))], approve: true);
        var designer = await ClientAsync(UserRole.Designer, "ВК");

        (await designer.GetAsync($"/api/clash/reports/{run}")).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await RowsAsync(designer, run)).Total.ShouldBe(1);
        (await designer.GetAsync($"/api/clash/parts/{partId}/approved/runs")).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await designer.GetAsync($"/api/clash/reports/{run}/chat?baseUrl=http://toadflow.local")).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Test]
    public async Task Report_Rows_LastNote()
    {
        var (admin, agent, partId, engine) = await SetupAsync();
        await using var _ = agent;
        var run = await RunAsync(admin, engine, partId, [Fp(Hit(1)), Fp(Hit(2))], approve: true);
        var id = (await RowsAsync(admin, run, "scope=open&q=1")).Items.Single().Collision.Id;
        (await admin.PostJsonAsync($"/api/clash/collisions/{id}/notes", new { text = "Первое" })).EnsureSuccessStatusCode();
        (await admin.PostJsonAsync($"/api/clash/collisions/{id}/notes", new { text = "Переносим трубу" })).EnsureSuccessStatusCode();

        var rows = (await RowsAsync(admin, run)).Items;

        (rows[0].LastNote, rows[0].LastNoteBy).ShouldBe(("Переносим трубу", "Администратор"));
        rows[0].LastNoteAt.ShouldNotBeNull();
        (rows[1].LastNote, rows[1].LastNoteBy, rows[1].LastNoteAt).ShouldBe((null, null, null));
    }

    [Test]
    public async Task Runs_List_NewestFirstWithSummary()
    {
        var (admin, agent, partId, engine) = await SetupAsync();
        await using var _ = agent;
        var first = await RunAsync(admin, engine, partId, [Fp(Hit(1)), Fp(Hit(2))], approve: true);
        var second = await RunAsync(admin, engine, partId, [Fp(Hit(1))]);
        await admin.PostAsync($"/api/clash/parts/{partId}/draft/run", null); // черновик в список не попадает

        var runs = await admin.GetJsonAsync<ReportRunView[]>($"/api/clash/parts/{partId}/approved/runs");

        runs.Select(r => r.RunId).ShouldBe([second, first]);
        runs[0].Summary.ShouldNotBeNull().Fixed.ShouldBe(1);
        runs[1].Summary.ShouldNotBeNull().New.ShouldBe(2);
    }

    [Test]
    public async Task List_UsesSharedFilter_SameResults()
    {
        var (admin, agent, partId, engine) = await SetupAsync();
        await using var _ = agent;
        await RunAsync(admin, engine, partId, [Fp(Hit(1)), Fp(Hit(2, disciplineA: "ВК", disciplineB: "КР"))], approve: true);

        async Task<int[]> NumbersAsync(string query) =>
            (await admin.GetJsonAsync<CollisionPage>($"/api/clash/parts/{partId}/approved/collisions?{query}")).Items.Select(c => c.Number).ToArray();

        (await NumbersAsync("discipline=КР")).ShouldBe([2]);
        (await NumbersAsync("pair=" + Uri.EscapeDataString("АР × ОВ"))).ShouldBe([1]);
        (await NumbersAsync("")).ShouldBe([1, 2]);
    }

    [Test]
    public void CategoryNames_MatchWeb()
    {
        var folder = new DirectoryInfo(AppContext.BaseDirectory);
        while (folder is not null && !File.Exists(Path.Combine(folder.FullName, "web", "src", "lib", "categories.ts"))) folder = folder.Parent;
        var source = File.ReadAllText(Path.Combine(folder.ShouldNotBeNull().FullName, "web", "src", "lib", "categories.ts"));
        var web = Regex.Matches(source, @"^\s*(OST_\w+):\s*'([^']*)',", RegexOptions.Multiline).ToDictionary(m => m.Groups[1].Value, m => m.Groups[2].Value);

        web.Count.ShouldBeGreaterThan(50);
        CategoryNames.All.ShouldBe(web, ignoreOrder: true);
        CategoryNames.Label("OST_Walls").ShouldBe("Стены");
        CategoryNames.Label("OST_Unknown").ShouldBe("OST_Unknown");
    }
}
