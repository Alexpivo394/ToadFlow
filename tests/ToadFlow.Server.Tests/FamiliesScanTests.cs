using System.Net;
using Microsoft.EntityFrameworkCore;
using Shouldly;
using ToadFlow.Contracts;
using ToadFlow.Contracts.Families;
using ToadFlow.Server.Families.Cards;
using ToadFlow.Server.Families.Data;
using ToadFlow.Server.Tests.Infrastructure;
using static ToadFlow.Server.Tests.Infrastructure.FamiliesFixtures;

namespace ToadFlow.Server.Tests;

public class FamiliesScanTests : ServerTest
{
    private const string Review = "_На проверке";

    public FamiliesScanTests() => Services = WithoutScanLoop;

    private FamilyLibrary _library = null!;

    [After(Test)]
    public async Task StopAgentAsync()
    {
        if (_library is not null) await _library.DisposeAsync();
    }

    private async Task<FamilyLibrary> LibraryAsync() => _library = await FamilyLibrary.StartAsync(Server, await AdminAsync());

    private Task<List<FamilyCard>> CardsAsync() => DbAsync(db => db.Set<FamilyCard>().AsNoTracking().OrderBy(c => c.CreatedAt).ToListAsync());

    private Task<List<FamilyVersion>> VersionsAsync(Guid cardId) =>
        DbAsync(db => db.Set<FamilyVersion>().AsNoTracking().Where(v => v.CardId == cardId).OrderBy(v => v.SeenAt).ThenBy(v => v.Id).ToListAsync());

    private Task<List<FamilyIssue>> OpenIssuesAsync() => DbAsync(db => db.Set<FamilyIssue>().AsNoTracking().Where(i => i.ResolvedAt == null).ToListAsync());

    [Test]
    public async Task NewFile_CreatesApprovedCardWithPendingRead()
    {
        var library = await LibraryAsync();

        await library.ScanAsync(File(@"ОВ\Клапаны\Клапан обратный.rfa", "a", year: 2022));

        var card = (await CardsAsync()).ShouldHaveSingleItem();
        (card.Name, card.Section, card.State, card.RevitYear, card.RelativePath).ShouldBe(("Клапан обратный", "ОВ", CardState.Approved, 2022, @"ОВ\Клапаны\Клапан обратный.rfa"));
        var version = (await VersionsAsync(card.Id)).ShouldHaveSingleItem();
        (version.State, version.ReadState, version.Hash, version.Note).ShouldBe((VersionState.Approved, ReadState.Pending, Hash("a"), ""));
        card.CurrentVersionId.ShouldBe(version.Id);
    }

    [Test]
    public async Task FileInReviewFolder_CreatesInReviewCard()
    {
        var library = await LibraryAsync();

        await library.ScanAsync(File($@"{Review}\ВК\Кран.rfa", "a"));

        var card = (await CardsAsync()).ShouldHaveSingleItem();
        (card.State, card.Section).ShouldBe((CardState.InReview, "ВК"));
        (await VersionsAsync(card.Id)).ShouldHaveSingleItem().State.ShouldBe(VersionState.InReview);
    }

    [Test]
    public async Task ChangedFile_AddsVersion_MarkedUnreviewed_AndIssue()
    {
        var library = await LibraryAsync();
        await library.ScanAsync(File(@"ОВ\Клапан.rfa", "a"));

        await library.ScanAsync(File(@"ОВ\Клапан.rfa", "b"));

        var card = (await CardsAsync()).ShouldHaveSingleItem();
        var versions = await VersionsAsync(card.Id);
        versions.Count.ShouldBe(2);
        card.CurrentVersionId.ShouldBe(versions[1].Id);
        (versions[1].Hash, versions[1].Note, versions[1].State, versions[1].ReadState).ShouldBe((Hash("b"), "изменено без проверки", VersionState.Approved, ReadState.Pending));
        var issue = (await OpenIssuesAsync()).ShouldHaveSingleItem();
        (issue.Kind, issue.CardId, issue.Hash).ShouldBe((IssueKind.UnreviewedChange, card.Id, Hash("b")));
    }

    [Test]
    public async Task CatalogOnlyChange_AddsVersionWithNote_NoIssue()
    {
        var library = await LibraryAsync();
        await library.ScanAsync(File(@"ОВ\Клапан.rfa", "a", catalog: "типы 1"));

        await library.ScanAsync(File(@"ОВ\Клапан.rfa", "a", catalog: "типы 2"));

        var card = (await CardsAsync()).ShouldHaveSingleItem();
        var versions = await VersionsAsync(card.Id);
        versions.Count.ShouldBe(2);
        (versions[1].Hash, versions[1].CatalogHash, versions[1].Note).ShouldBe((Hash("a"), Hash("типы 2"), "изменён каталог типоразмеров"));
        card.CurrentVersionId.ShouldBe(versions[1].Id);
        (await OpenIssuesAsync()).ShouldBeEmpty();
    }

    [Test]
    public async Task MovedOutOfReview_BecomesApproved_SameCard()
    {
        var library = await LibraryAsync();
        await library.ScanAsync(File($@"{Review}\Кран.rfa", "a"));
        var before = (await CardsAsync()).ShouldHaveSingleItem();

        await library.ScanAsync(File(@"ВК\Арматура\Кран.rfa", "a"));

        var card = (await CardsAsync()).ShouldHaveSingleItem();
        card.Id.ShouldBe(before.Id);
        (card.State, card.RelativePath, card.Section).ShouldBe((CardState.Approved, @"ВК\Арматура\Кран.rfa", "ВК"));
        (await VersionsAsync(card.Id)).ShouldHaveSingleItem().State.ShouldBe(VersionState.Approved);
    }

    [Test]
    public async Task MovedWithCatalogChange_AddsCatalogVersion()
    {
        var library = await LibraryAsync();
        await library.ScanAsync(File(@"ОВ\Кран.rfa", "a", catalog: "типы 1"));

        await library.ScanAsync(File(@"ВК\Кран.rfa", "a"));

        var card = (await CardsAsync()).ShouldHaveSingleItem();
        card.RelativePath.ShouldBe(@"ВК\Кран.rfa");
        var versions = await VersionsAsync(card.Id);
        versions.Count.ShouldBe(2);
        (versions[1].CatalogHash, versions[1].Note, versions[1].RelativePath).ShouldBe((null, "изменён каталог типоразмеров", @"ВК\Кран.rfa"));
        card.CurrentVersionId.ShouldBe(versions[1].Id);
    }

    [Test]
    public async Task CaseOnlyRename_KeepsCardWithNewCasing()
    {
        var library = await LibraryAsync();
        await library.ScanAsync(File(@"ОВ\клапан.rfa", "a"));

        await library.ScanAsync(File(@"ОВ\Клапан.rfa", "a"));

        var card = (await CardsAsync()).ShouldHaveSingleItem();
        (card.Name, card.RelativePath).ShouldBe(("Клапан", @"ОВ\Клапан.rfa"));
        (await VersionsAsync(card.Id)).Count.ShouldBe(1);
    }

    [Test]
    public async Task CandidateOfApprovedCard_KeepsCurrentVersion()
    {
        var library = await LibraryAsync();
        await library.ScanAsync(File(@"ОВ\Клапан.rfa", "a"));

        await library.ScanAsync(File(@"ОВ\Клапан.rfa", "a"), File($@"{Review}\Клапан.rfa", "b"));

        var card = (await CardsAsync()).ShouldHaveSingleItem();
        card.State.ShouldBe(CardState.Approved);
        var versions = await VersionsAsync(card.Id);
        versions.Count.ShouldBe(2);
        card.CurrentVersionId.ShouldBe(versions[0].Id);
        (versions[1].State, versions[1].RelativePath, versions[1].Hash).ShouldBe((VersionState.InReview, $@"{Review}\Клапан.rfa", Hash("b")));
    }

    [Test]
    public async Task CandidateMovedInsideReview_SameVersionNewPath()
    {
        var library = await LibraryAsync();
        await library.ScanAsync(File(@"ОВ\Клапан.rfa", "a")); // карточка есть — файл в папке проверки станет её кандидатом
        await library.ScanAsync(File(@"ОВ\Клапан.rfa", "a"), File($@"{Review}\Клапан.rfa", "b"));

        await library.ScanAsync(File(@"ОВ\Клапан.rfa", "a"), File($@"{Review}\2026-11\Клапан.rfa", "b"));

        var card = (await CardsAsync()).ShouldHaveSingleItem();
        var versions = await VersionsAsync(card.Id);
        versions.Count.ShouldBe(2);
        (versions[1].State, versions[1].RelativePath).ShouldBe((VersionState.InReview, $@"{Review}\2026-11\Клапан.rfa"));
    }

    [Test]
    public async Task CandidateMovedOverCurrentFile_BecomesCurrent_NotMarkedUnreviewed()
    {
        var library = await LibraryAsync();
        await library.ScanAsync(File(@"ОВ\Клапан.rfa", "a")); // карточка есть — файл в папке проверки станет её кандидатом
        await library.ScanAsync(File(@"ОВ\Клапан.rfa", "a"), File($@"{Review}\Клапан.rfa", "b"));

        await library.ScanAsync(File(@"ОВ\Клапан.rfa", "b"));

        var card = (await CardsAsync()).ShouldHaveSingleItem();
        var versions = await VersionsAsync(card.Id);
        versions.Count.ShouldBe(2);
        card.CurrentVersionId.ShouldBe(versions[1].Id);
        (versions[1].State, versions[1].Note, versions[1].RelativePath).ShouldBe((VersionState.Approved, "", @"ОВ\Клапан.rfa"));
        (await OpenIssuesAsync()).ShouldBeEmpty();
    }

    [Test]
    public async Task CandidateApprovedIntoNewFolder_BecomesCurrent()
    {
        var library = await LibraryAsync();
        await library.ScanAsync(File(@"ОВ\Клапан.rfa", "a")); // карточка есть — файл в папке проверки станет её кандидатом
        await library.ScanAsync(File(@"ОВ\Клапан.rfa", "a"), File($@"{Review}\Клапан.rfa", "b"));

        await library.ScanAsync(File(@"КР\Клапан.rfa", "b"));

        var card = (await CardsAsync()).ShouldHaveSingleItem();
        (card.State, card.RelativePath, card.Section).ShouldBe((CardState.Approved, @"КР\Клапан.rfa", "КР"));
        var versions = await VersionsAsync(card.Id);
        versions.Count.ShouldBe(2);
        card.CurrentVersionId.ShouldBe(versions[1].Id);
        (versions[1].State, versions[1].Note).ShouldBe((VersionState.Approved, ""));
    }

    [Test]
    public async Task CandidateRemovedFromReview_Rejected()
    {
        var library = await LibraryAsync();
        await library.ScanAsync(File(@"ОВ\Клапан.rfa", "a")); // карточка есть — файл в папке проверки станет её кандидатом
        await library.ScanAsync(File(@"ОВ\Клапан.rfa", "a"), File($@"{Review}\Клапан.rfa", "b"));

        await library.ScanAsync(File(@"ОВ\Клапан.rfa", "a"));

        var card = (await CardsAsync()).ShouldHaveSingleItem();
        var versions = await VersionsAsync(card.Id);
        versions[1].State.ShouldBe(VersionState.Rejected);
        card.CurrentVersionId.ShouldBe(versions[0].Id);
    }

    [Test]
    public async Task NameConflict_NoSecondCard_OneIssue_NotDuplicatedOnNextScan()
    {
        var library = await LibraryAsync();
        await library.ScanAsync(File(@"ОВ\Клапан.rfa", "a"));

        await library.ScanAsync(File(@"ОВ\Клапан.rfa", "a"), File(@"ВК\Клапан.rfa", "b"));
        await library.ScanAsync(File(@"ОВ\Клапан.rfa", "a"), File(@"ВК\Клапан.rfa", "b"));

        (await CardsAsync()).ShouldHaveSingleItem();
        var issue = (await OpenIssuesAsync()).ShouldHaveSingleItem();
        (issue.Kind, issue.RelativePath).ShouldBe((IssueKind.NameConflict, @"ВК\Клапан.rfa"));
        issue.Text.ShouldContain(@"ОВ\Клапан.rfa");
        issue.Text.ShouldContain("Переименуйте");
    }

    [Test]
    public async Task Duplicate_NoSecondCard_Issue()
    {
        var library = await LibraryAsync();
        await library.ScanAsync(File(@"ОВ\Клапан.rfa", "a"));

        await library.ScanAsync(File(@"ОВ\Клапан.rfa", "a"), File(@"ВК\Копия клапана.rfa", "a"));

        (await CardsAsync()).ShouldHaveSingleItem();
        (await OpenIssuesAsync()).ShouldHaveSingleItem().Kind.ShouldBe(IssueKind.Duplicate);
    }

    [Test]
    public async Task ResolvedCause_ClosesIssue()
    {
        var library = await LibraryAsync();
        await library.ScanAsync(File(@"ОВ\Клапан.rfa", "a"));
        await library.ScanAsync(File(@"ОВ\Клапан.rfa", "a"), File(@"ВК\Клапан.rfa", "b"));

        await library.ScanAsync(File(@"ОВ\Клапан.rfa", "a"));

        (await OpenIssuesAsync()).ShouldBeEmpty();
        (await DbAsync(db => db.Set<FamilyIssue>().CountAsync(i => i.ResolvedAt != null))).ShouldBe(1);
    }

    [Test]
    public async Task Removed_Archives_AndReturnOfSameFile_Restores()
    {
        var library = await LibraryAsync();
        await library.ScanAsync(File(@"ОВ\Клапан.rfa", "a"));

        await library.ScanAsync();
        var archived = (await CardsAsync()).ShouldHaveSingleItem();
        archived.State.ShouldBe(CardState.Archived);
        archived.ArchivedAt.ShouldNotBeNull();

        await library.ScanAsync(File(@"ОВ\Клапан.rfa", "a"));

        var card = (await CardsAsync()).ShouldHaveSingleItem();
        card.Id.ShouldBe(archived.Id);
        (card.State, card.ArchivedAt).ShouldBe((CardState.Approved, null));
        (await VersionsAsync(card.Id)).ShouldHaveSingleItem();
    }

    [Test]
    public async Task IncompleteScan_ArchivesNothing()
    {
        var library = await LibraryAsync();
        await library.ScanAsync(File(@"ОВ\Клапан.rfa", "a"));

        await library.ScanAsync([], complete: false);

        (await CardsAsync()).ShouldHaveSingleItem().State.ShouldBe(CardState.Approved);
    }

    [Test]
    public async Task UnsettledKnownFile_CardUnchanged()
    {
        var library = await LibraryAsync();
        await library.ScanAsync(File(@"ОВ\Клапан.rfa", "a"));

        await library.ScanAsync([], complete: true, unsettled: [@"ОВ\Клапан.rfa"]);

        var card = (await CardsAsync()).ShouldHaveSingleItem();
        card.State.ShouldBe(CardState.Approved);
        (await VersionsAsync(card.Id)).ShouldHaveSingleItem();
    }

    [Test]
    public async Task RenameChain_AppliedWithoutUniqueIndexError()
    {
        var library = await LibraryAsync();
        await library.ScanAsync(File(@"X\Клапан.rfa", "a"), File(@"Y\Кран.rfa", "b"));

        await library.ScanAsync(File(@"X\Кран.rfa", "a"), File(@"Y\Насос.rfa", "b"));

        var cards = await CardsAsync();
        cards.Select(c => (c.Name, c.RelativePath, c.State)).OrderBy(c => c.RelativePath)
            .ShouldBe([("Кран", @"X\Кран.rfa", CardState.Approved), ("Насос", @"Y\Насос.rfa", CardState.Approved)]);
    }

    [Test]
    public async Task Ambiguous_KeepsCardAndAsks()
    {
        var library = await LibraryAsync();
        await library.ScanAsync(File(@"ОВ\Клапан.rfa", "a"));

        await library.ScanAsync(File(@"ВК\Клапан новый.rfa", "b"));

        (await CardsAsync()).ShouldHaveSingleItem().State.ShouldBe(CardState.Approved);
        var issue = (await OpenIssuesAsync()).ShouldHaveSingleItem();
        (issue.Kind, issue.RelativePath).ShouldBe((IssueKind.Ambiguous, @"ВК\Клапан новый.rfa"));
    }

    [Test]
    public async Task OpenAmbiguous_StaysOpenWhenAnotherFileAppears()
    {
        var library = await LibraryAsync();
        await library.ScanAsync(File(@"ОВ\Клапан.rfa", "a"));
        await library.ScanAsync(File(@"ВК\Клапан новый.rfa", "b"));

        await library.ScanAsync(File(@"ВК\Клапан новый.rfa", "b"), File(@"КР\Балка.rfa", "c"));

        var cards = await CardsAsync();
        cards.Single(c => c.Name == "Клапан").State.ShouldBe(CardState.Approved);
        cards.ShouldContain(c => c.Name == "Балка");
        (await OpenIssuesAsync()).ShouldHaveSingleItem().Kind.ShouldBe(IssueKind.Ambiguous);
    }

    [Test]
    public async Task SameReportTwice_AppliedOnce()
    {
        var library = await LibraryAsync();
        var scanId = await library.RequestScanAsync();
        var report = new FamilyScanReportDto(scanId, true, [File(@"ОВ\Клапан.rfa", "a")], [], []);

        (await library.ReportAsync(report)).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await library.ReportAsync(report)).StatusCode.ShouldBe(HttpStatusCode.OK);

        var card = (await CardsAsync()).ShouldHaveSingleItem();
        (await VersionsAsync(card.Id)).ShouldHaveSingleItem();
    }

    [Test]
    public async Task ReportOfUnknownScan_Ignored()
    {
        var library = await LibraryAsync();

        var response = await library.ReportAsync(new FamilyScanReportDto(Guid.NewGuid(), true, [File(@"ОВ\Клапан.rfa", "a")], [], []));

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await CardsAsync()).ShouldBeEmpty();
    }

    [Test]
    public async Task ReportOfAbandonedScan_IgnoredAfterNewScanStarted()
    {
        var library = await LibraryAsync();
        var old = await library.RequestScanAsync();
        // Агент молчал дольше получаса — сервер считает обход потерянным и просит новый
        await DbAsync(db => db.Set<FamilyScan>().Where(s => s.Id == old)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.StartedAt, DateTimeOffset.UtcNow.AddHours(-1))));
        var current = await library.RequestScanAsync();

        await library.ReportAsync(new FamilyScanReportDto(old, true, [File(@"ОВ\Старый.rfa", "a")], [], []));
        await library.ReportAsync(new FamilyScanReportDto(current, true, [File(@"ОВ\Новый.rfa", "b")], [], []));

        (await CardsAsync()).ShouldHaveSingleItem().Name.ShouldBe("Новый");
    }

    [Test]
    public async Task ScanNow_WhileScanRunning_409()
    {
        var library = await LibraryAsync();
        await library.RequestScanAsync();

        var response = await library.Admin.PostAsync("/api/families/scan", null);

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
    }

    [Test]
    public async Task ScanNow_ByViewer_403()
    {
        await LibraryAsync();

        var response = await (await ClientAsync(UserRole.Viewer)).PostAsync("/api/families/scan", null);

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Test]
    public async Task ScanNow_ByOperator_Accepted()
    {
        var library = await LibraryAsync();

        var response = await (await ClientAsync(UserRole.Operator)).PostAsync("/api/families/scan", null);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        library.Agent.ScanRequests.ShouldHaveSingleItem().Root.ShouldBe(Root);
    }

    [Test]
    public async Task NoAgentWithFamilies_StatusSaysAgentOffline()
    {
        // Агент прошлой версии: выгрузку, LOI и коллизии берёт, а обходить библиотеку не умеет
        _library = await FamilyLibrary.StartAsync(Server, await AdminAsync(), modules: [ModuleKinds.Export, ModuleKinds.Loi, ModuleKinds.ClashExtract]);

        var status = await _library.Admin.GetJsonAsync<LibraryStatus>("/api/families/status");
        var scan = await _library.Admin.PostAsync("/api/families/scan", null);

        (status.Configured, status.AgentOnline, status.AgentName).ShouldBe((true, false, null));
        scan.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        _library.Agent.ScanRequests.ShouldBeEmpty();
    }

    [Test]
    public async Task OldAgentWithoutModuleList_NeverAskedToScan()
    {
        var admin = await AdminAsync();
        await using var old = await FakeAgent.RegisterAsync(Server, admin, "OLD-AGENT");
        await old.ConnectAsync("1.8.0", new InstalledRevitDto(2022, true));
        (await admin.PutJsonAsync("/api/families/settings", new ToadFlow.Server.Families.FamiliesSettings(Root))).EnsureSuccessStatusCode();

        var scan = await admin.PostAsync("/api/families/scan", null);

        scan.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        old.ScanRequests.ShouldBeEmpty();
    }

    [Test]
    public async Task Status_AfterScan_ShowsLastScanAndPendingReads()
    {
        var library = await LibraryAsync();
        await library.ScanAsync([File(@"ОВ\Клапан.rfa", "a")], complete: false, errors: ["КР: нет доступа"]);

        var status = await library.Admin.GetJsonAsync<LibraryStatus>("/api/families/status");

        (status.Configured, status.AgentName, status.AgentOnline, status.LastScanComplete, status.PendingReads, status.FailedReads)
            .ShouldBe((true, "BIM-LIB-01", true, false, 1, 0));
        status.LastScanAt.ShouldNotBeNull();
        status.LastScanErrors.ShouldBe(["КР: нет доступа"]);
    }

    [Test]
    public async Task Review_ListsNewFamiliesAndCandidates()
    {
        var library = await LibraryAsync();
        await library.ScanAsync(File(@"ОВ\Клапан.rfa", "a")); // карточка есть — файл в папке проверки станет её кандидатом
        await library.ScanAsync(File(@"ОВ\Клапан.rfa", "a"), File($@"{Review}\Клапан.rfa", "b"), File($@"{Review}\Кран.rfa", "c"));

        var items = await library.Admin.GetJsonAsync<FamilyReviewItem[]>("/api/families/review");

        items.Select(i => (i.Name, i.IsNew, i.RelativePath)).OrderBy(i => i.Name)
            .ShouldBe([("Клапан", false, $@"{Review}\Клапан.rfa"), ("Кран", true, $@"{Review}\Кран.rfa")]);
    }

    [Test]
    public async Task Issues_ListedOpenWithCardName()
    {
        var library = await LibraryAsync();
        await library.ScanAsync(File(@"ОВ\Клапан.rfa", "a"));
        await library.ScanAsync(File(@"ОВ\Клапан.rfa", "a"), File(@"ВК\Клапан.rfa", "b"));

        var issues = await (await ClientAsync(UserRole.Viewer)).GetJsonAsync<FamilyIssueView[]>("/api/families/issues?open=true");

        var issue = issues.ShouldHaveSingleItem();
        (issue.Kind, issue.CardName, issue.RelativePath).ShouldBe((IssueKind.NameConflict, "Клапан", @"ВК\Клапан.rfa"));
    }
}

public class FamiliesAgentsTests : ServerTest
{
    public FamiliesAgentsTests() => Services = WithoutScanLoop;

    [Test]
    public async Task Agents_ListOnlyThoseThatCanScan()
    {
        var admin = await AdminAsync();
        await using var library = await FamilyLibrary.StartAsync(Server, admin);
        await using var old = await FakeAgent.RegisterAsync(Server, admin, "OLD-AGENT");
        await old.ConnectAsync("2.4.0", [ModuleKinds.Export], new InstalledRevitDto(2022, true));

        var agents = await (await ClientAsync(UserRole.Viewer)).GetJsonAsync<ToadFlow.Server.Families.Scan.LibraryAgent[]>("/api/families/agents");

        agents.ShouldBe([new ToadFlow.Server.Families.Scan.LibraryAgent(library.Agent.Id, "BIM-LIB-01", true)]);
    }
}
