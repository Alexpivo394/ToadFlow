using System.Net;
using System.Text.Json;
using Alexpivo394.Revit.Families.Files;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using ToadFlow.Contracts;
using ToadFlow.Contracts.Families;
using ToadFlow.Server.Data.Entities;
using ToadFlow.Server.Families.Cards;
using ToadFlow.Server.Families.Data;
using ToadFlow.Server.Families.Read;
using ToadFlow.Server.Families.Search;
using ToadFlow.Server.Runs;
using ToadFlow.Server.Tests.Infrastructure;
using static ToadFlow.Server.Tests.Infrastructure.FamiliesFixtures;

namespace ToadFlow.Server.Tests;

public class FamiliesReadTests : ServerTest
{
    public FamiliesReadTests() => Services = WithoutScanLoop;

    /// <summary>Наименьший настоящий PNG (1×1).</summary>
    private static readonly byte[] Png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNkYAAAAAYAAjCB0C8AAAAASUVORK5CYII=");

    private FamilyLibrary _library = null!;
    private readonly List<FakeAgent> _agents = [];

    [After(Test)]
    public async Task StopAgentsAsync()
    {
        if (_library is not null) await _library.DisposeAsync();
        foreach (var agent in _agents) await agent.DisposeAsync();
    }

    private async Task<FamilyLibrary> LibraryAsync(params InstalledRevitDto[] revits) => _library = await FamilyLibrary.StartAsync(Server, await AdminAsync(), revits: revits);

    private Task<List<Job>> ReadJobsAsync() => DbAsync(db => db.Jobs.AsNoTracking().Where(j => j.ModuleKind == ModuleKinds.FamiliesRead).OrderBy(j => j.CreatedAt).ToListAsync());

    private Task<FamilyCard> CardAsync(string name) => DbAsync(db => db.Set<FamilyCard>().AsNoTracking().SingleAsync(c => c.Name == name));

    private Task<List<FamilyVersion>> VersionsAsync(Guid cardId) =>
        DbAsync(db => db.Set<FamilyVersion>().AsNoTracking().Where(v => v.CardId == cardId).OrderBy(v => v.SeenAt).ThenBy(v => v.Id).ToListAsync());

    private Task SweepAsync() => DbAsync(async db =>
    {
        await using var scope = Server.Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<FamilyReadJobs>().SweepAsync(CancellationToken.None);
    });

    private static FamilyContent Content(params string[] types) =>
        new("Арматура трубопроводов", "OneLevelBased", 2022, types.Select(t => new FamilyTypeInfo(t, new Dictionary<string, string>())).ToList(),
            [new FamilyParameterInfo("Диаметр условный", false, null, false, "Размеры", "Длина")], false);

    /// <summary>Агент берёт задание чтения и сдаёт его.</summary>
    private static async Task<JobAssignmentDto> CompleteAsync(FakeAgent agent, int year, JobOutcome outcome, string? note = null, FamilyReadResult? result = null)
    {
        var assignment = (await agent.RequestJobAsync(1, year)).ShouldNotBeNull();
        assignment.ModuleKind.ShouldBe(ModuleKinds.FamiliesRead);
        await agent.CompleteJobAsync(assignment.JobId, outcome, note, result is null ? null : JsonSerializer.Serialize(result, TestJson.Options), assignment.Attempt);
        return assignment;
    }

    [Test]
    public async Task NewVersion_EnqueuesReadJob_ForFileYear()
    {
        var library = await LibraryAsync();

        await library.ScanAsync(File(@"ОВ\Клапан.rfa", "a", catalog: "типы", year: 2024));

        var job = (await ReadJobsAsync()).ShouldHaveSingleItem();
        var card = await CardAsync("Клапан");
        (job.RevitYear, job.ModelName, job.ModelPath, job.MaxAttempts, job.TimeoutMinutes, job.Priority, job.Status)
            .ShouldBe((2024, "Клапан", @"\\nas\Families\ОВ\Клапан.rfa", 2, 10, 0, JobStatus.Queued));
        var payload = JsonSerializer.Deserialize<FamilyReadPayload>(job.PayloadJson, TestJson.Options).ShouldNotBeNull();
        payload.ShouldBe(new FamilyReadPayload(card.CurrentVersionId!.Value, @"\\nas\Families\ОВ\Клапан.rfa", Hash("a"), @"\\nas\Families\ОВ\Клапан.txt"));
        var run = await DbAsync(db => db.Runs.AsNoTracking().SingleAsync(r => r.Id == job.RunId));
        (run.ModuleName, run.Name).ShouldBe(("families", "Чтение семейства Клапан"));
    }

    [Test]
    public async Task VersionsOfOneScan_OneRun()
    {
        var library = await LibraryAsync();

        await library.ScanAsync(File(@"ОВ\Клапан.rfa", "a"), File(@"ОВ\Кран.rfa", "b"), File(@"КР\Балка.rfa", "c"));

        (await ReadJobsAsync()).Select(j => j.RunId).Distinct().ShouldHaveSingleItem();
        (await DbAsync(db => db.Runs.AsNoTracking().SingleAsync())).Name.ShouldBe("Чтение семейств: 3");
    }

    [Test]
    public async Task NoAgentOfThatYear_UsesNearestHigher()
    {
        var library = await LibraryAsync(new InstalledRevitDto(2022, true), new InstalledRevitDto(2024, true));

        await library.ScanAsync(File(@"ОВ\Клапан.rfa", "a", year: 2023), File(@"ОВ\Кран.rfa", "b", year: 2021), File(@"ОВ\Отвод.rfa", "c", year: null));

        var jobs = await ReadJobsAsync();
        jobs.Single(j => j.ModelName == "Клапан").RevitYear.ShouldBe(2024);
        jobs.Single(j => j.ModelName == "Кран").RevitYear.ShouldBe(2022);
        jobs.Single(j => j.ModelName == "Отвод").RevitYear.ShouldBe(2024);
    }

    [Test]
    public async Task OnlyOlderRevit_NoJob()
    {
        var library = await LibraryAsync(new InstalledRevitDto(2022, true));

        await library.ScanAsync(File(@"ОВ\Клапан.rfa", "a", year: 2024));

        (await ReadJobsAsync()).ShouldBeEmpty();
        (await VersionsAsync((await CardAsync("Клапан")).Id)).ShouldHaveSingleItem().ReadState.ShouldBe(ReadState.Pending);
    }

    [Test]
    public async Task NoAgentAtAll_StaysPending_AndIsEnqueuedWhenAgentAppears()
    {
        // Агент библиотеки умеет только обход: Revit на нём нет
        _library = await FamilyLibrary.StartAsync(Server, await AdminAsync(), modules: [ModuleKinds.FamiliesScan]);
        await _library.ScanAsync(File(@"ОВ\Клапан.rfa", "a"));
        (await ReadJobsAsync()).ShouldBeEmpty();
        await SweepAsync();
        (await ReadJobsAsync()).ShouldBeEmpty();

        var farm = await FakeAgent.RegisterAsync(Server, _library.Admin, "BIM-FARM-02");
        _agents.Add(farm);
        await farm.ConnectAsync("2.5.0", AgentModules, new InstalledRevitDto(2022, true));
        await SweepAsync();
        await SweepAsync();

        (await ReadJobsAsync()).ShouldHaveSingleItem().RevitYear.ShouldBe(2022);
    }

    [Test]
    public async Task OldAgents_NeverGetFamiliesRead()
    {
        _library = await FamilyLibrary.StartAsync(Server, await AdminAsync(), modules: [ModuleKinds.FamiliesScan]);
        var old = await FakeAgent.RegisterAsync(Server, _library.Admin, "OLD-1");
        var older = await FakeAgent.RegisterAsync(Server, _library.Admin, "OLD-2");
        _agents.AddRange([old, older]);
        await old.ConnectAsync("1.8.0", new InstalledRevitDto(2022, true)); // до 2.0: без списка видов
        await older.ConnectAsync("2.4.0", [ModuleKinds.Export, ModuleKinds.Loi, ModuleKinds.LoiCatalog, ModuleKinds.ClashExtract], new InstalledRevitDto(2022, true));
        await _library.ScanAsync(File(@"ОВ\Клапан.rfa", "a"));
        await SweepAsync();

        (await ReadJobsAsync()).ShouldBeEmpty();
        (await old.RequestJobAsync(1, 2022)).ShouldBeNull();
        (await older.RequestJobAsync(1, 2022)).ShouldBeNull();
    }

    [Test]
    public async Task ReadSucceeded_FillsCardAndSearch()
    {
        var library = await LibraryAsync();
        await library.ScanAsync(File(@"ОВ\Клапан.rfa", "a"));

        await CompleteAsync(library.Agent, 2022, JobOutcome.Succeeded, result: new FamilyReadResult(Content("КЛАБ-40-20", "КЛАБ-50-20"), Convert.ToBase64String(Png)));

        var card = await CardAsync("Клапан");
        (card.Category, card.Placement, card.TypeCount).ShouldBe(("Арматура трубопроводов", "OneLevelBased", 2));
        var version = (await VersionsAsync(card.Id)).ShouldHaveSingleItem();
        (version.ReadState, version.HasPreview, version.ReadError).ShouldBe((ReadState.Done, true, null));
        var page = await (await ClientAsync(UserRole.Designer)).GetJsonAsync<Page<FamilyHit>>("/api/families/search?q=клаб-50");
        page.Items.ShouldHaveSingleItem().PreviewHash.ShouldBe(Hash("a"));
        var view = await library.Admin.GetJsonAsync<FamilyCardView>($"/api/families/{card.Id}");
        view.Content.ShouldNotBeNull().Types.Select(t => t.Name).ShouldBe(["КЛАБ-40-20", "КЛАБ-50-20"]);
        // Картинка лежит в хранилище, в итоге задания её больше нет
        var job = (await ReadJobsAsync()).ShouldHaveSingleItem();
        job.ResultJson.ShouldBeNull();
    }

    [Test]
    public async Task ReadFailed_MarksVersionAndCreatesIssue()
    {
        var library = await LibraryAsync();
        await library.ScanAsync(File(@"ОВ\Клапан.rfa", "a"));

        await CompleteAsync(library.Agent, 2022, JobOutcome.Failed, "Revit не открыл файл: The file is corrupt.");

        var card = await CardAsync("Клапан");
        var version = (await VersionsAsync(card.Id)).ShouldHaveSingleItem();
        (version.ReadState, version.ReadError).ShouldBe((ReadState.Failed, "Revit не открыл файл: The file is corrupt."));
        var issue = (await DbAsync(db => db.Set<FamilyIssue>().AsNoTracking().ToListAsync())).ShouldHaveSingleItem();
        (issue.Kind, issue.CardId).ShouldBe((IssueKind.Unreadable, card.Id));
        issue.Text.ShouldContain("The file is corrupt.");
        var status = await library.Admin.GetJsonAsync<LibraryStatus>("/api/families/status");
        (status.PendingReads, status.FailedReads).ShouldBe((0, 1));
    }

    [Test]
    public async Task ReadInfrastructureFailedEveryAttempt_MarkedFailedBySweep_NotRequeuedForever()
    {
        var library = await LibraryAsync();
        await library.ScanAsync(File(@"ОВ\Клапан.rfa", "a"));

        await CompleteAsync(library.Agent, 2022, JobOutcome.InfrastructureFailure, "Revit упал");
        await CompleteAsync(library.Agent, 2022, JobOutcome.InfrastructureFailure, "Revit упал");
        await SweepAsync();

        (await ReadJobsAsync()).ShouldHaveSingleItem().Status.ShouldBe(JobStatus.Failed);
        (await VersionsAsync((await CardAsync("Клапан")).Id)).ShouldHaveSingleItem().ReadState.ShouldBe(ReadState.Failed);
    }

    [Test]
    public async Task ReadOfCandidate_DoesNotChangeCardFields()
    {
        var library = await LibraryAsync();
        await library.ScanAsync(File(@"ОВ\Клапан.rfa", "a"));
        await CompleteAsync(library.Agent, 2022, JobOutcome.Succeeded, result: new FamilyReadResult(Content("DN50"), null));
        await library.ScanAsync(File(@"ОВ\Клапан.rfa", "a"), File(@"_На проверке\Клапан.rfa", "b"));

        await CompleteAsync(library.Agent, 2022, JobOutcome.Succeeded,
            result: new FamilyReadResult(Content("DN50", "DN80", "DN100") with { Category = "Оборудование" }, null));

        var card = await CardAsync("Клапан");
        (card.Category, card.TypeCount).ShouldBe(("Арматура трубопроводов", 1));
        (await VersionsAsync(card.Id)).Select(v => v.ReadState).ShouldBe([ReadState.Done, ReadState.Done]);
    }

    [Test]
    public async Task Preview_ServedAsPng_AndHiddenLikeCard()
    {
        var library = await LibraryAsync();
        await library.ScanAsync(File(@"_На проверке\Кран.rfa", "a"));
        await CompleteAsync(library.Agent, 2022, JobOutcome.Succeeded, result: new FamilyReadResult(Content("A"), Convert.ToBase64String(Png)));
        var card = await CardAsync("Кран");
        var viewer = await ClientAsync(UserRole.Viewer);
        var designer = await ClientAsync(UserRole.Designer);

        var response = await viewer.GetAsync($"/api/families/{card.Id}/preview?version={Hash("a")}");

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        response.Content.Headers.ContentType!.MediaType.ShouldBe("image/png");
        response.Headers.CacheControl!.MaxAge.ShouldBe(TimeSpan.FromDays(365));
        (await response.Content.ReadAsByteArrayAsync()).ShouldBe(Png);
        (await designer.GetAsync($"/api/families/{card.Id}/preview")).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await designer.GetAsync($"/api/families/{card.Id}/preview?unreviewed=true")).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await viewer.GetAsync($"/api/families/{card.Id}/preview?version={Hash("x")}")).StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Test]
    public async Task FileChangedWarning_KeepsPending_NoRequeue()
    {
        var library = await LibraryAsync();
        await library.ScanAsync(File(@"ОВ\Клапан.rfa", "a"));

        await CompleteAsync(library.Agent, 2022, JobOutcome.Warning, "файл изменился после обхода");
        await SweepAsync();

        (await ReadJobsAsync()).ShouldHaveSingleItem();
        (await VersionsAsync((await CardAsync("Клапан")).Id)).ShouldHaveSingleItem().ReadState.ShouldBe(ReadState.Pending);
        (await library.Admin.GetJsonAsync<LibraryStatus>("/api/families/status")).PendingReads.ShouldBe(0);
    }

    [Test]
    public async Task QueuedJobOfVanishedYear_ReplacedByAvailableYear()
    {
        var library = await LibraryAsync(new InstalledRevitDto(2024, true));
        await library.ScanAsync(File(@"ОВ\Клапан.rfa", "a", year: 2022));
        (await ReadJobsAsync()).ShouldHaveSingleItem().RevitYear.ShouldBe(2024);
        var farm = await FakeAgent.RegisterAsync(Server, library.Admin, "BIM-FARM-02");
        _agents.Add(farm);
        await farm.ConnectAsync("2.5.0", AgentModules, new InstalledRevitDto(2022, true));
        await DbAsync(db => db.Agents.Where(a => a.Id == library.Agent.Id).ExecuteUpdateAsync(a => a.SetProperty(x => x.State, AgentState.Disabled)));

        await SweepAsync();

        var jobs = await ReadJobsAsync();
        jobs.Select(j => (j.RevitYear, j.Status)).ShouldBe([(2024, JobStatus.Cancelled), (2022, JobStatus.Queued)]);
    }
}
