using System.Net;
using Microsoft.EntityFrameworkCore;
using Shouldly;
using ToadFlow.Contracts;
using ToadFlow.Contracts.Families;
using ToadFlow.Server.Families.Actions;
using ToadFlow.Server.Families.Cards;
using ToadFlow.Server.Families.Data;
using ToadFlow.Server.Tests.Infrastructure;
using static ToadFlow.Server.Tests.Infrastructure.FamiliesFixtures;

namespace ToadFlow.Server.Tests;

/// <summary>Кнопки координатора, которые меняют файлы библиотеки через агента, и ответы в «Нужно решение».</summary>
public class FamiliesActionTests : ServerTest
{
    public FamiliesActionTests() => Services = WithoutScanLoop;

    private FamilyLibrary _library = null!;
    private HttpClient _coordinator = null!;

    [After(Test)]
    public async Task StopAgentAsync()
    {
        if (_library is not null) await _library.DisposeAsync();
    }

    private async Task StartAsync(IReadOnlyList<string>? modules = null)
    {
        _library = await FamilyLibrary.StartAsync(Server, await AdminAsync(), modules: modules);
        _coordinator = await ClientAsync(UserRole.Operator);
    }

    private async Task<FamilyCard> CardAsync(string name) => await DbAsync(db => db.Set<FamilyCard>().AsNoTracking().Where(c => c.Name == name).SingleAsync());

    private async Task<Guid> VersionIdAsync(string content) => await DbAsync(db => db.Set<FamilyVersion>().Where(v => v.Hash == Hash(content)).Select(v => v.Id).FirstAsync());

    private async Task<List<FamilyIssue>> OpenIssuesAsync() => await DbAsync(db => db.Set<FamilyIssue>().AsNoTracking().Where(i => i.ResolvedAt == null).ToListAsync());

    private async Task<string> ErrorAsync(HttpResponseMessage response)
    {
        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        return await response.Content.ReadAsStringAsync();
    }

    [Test]
    public async Task ApproveNew_MovesIntoFolder_CardKeepsItsHistory()
    {
        await StartAsync();
        await _library.ScanAsync(File(@"_На проверке\Задвижка.rfa", "z", "types"));
        var card = await CardAsync("Задвижка");

        (await _coordinator.PostJsonAsync($"/api/families/{card.Id}/approve", new ApproveRequest(@"ОВ\Арматура"))).EnsureSuccessStatusCode();

        var action = _library.Agent.FileActions.Single();
        (action.Kind, action.RelativePath, action.TargetPath, action.Hash, action.CatalogHash, action.ReplaceHash, action.Root)
            .ShouldBe((FamilyFileActionKind.Move, @"_На проверке\Задвижка.rfa", @"ОВ\Арматура\Задвижка.rfa", Hash("z"), Hash("types"), (string?)null, Root));
        _library.Agent.ScanRequests.Count.ShouldBe(2); // обход начат сразу
        await _library.ReportLastAsync(File(@"ОВ\Арматура\Задвижка.rfa", "z", "types"));
        var moved = await CardAsync("Задвижка");
        (moved.Id, moved.State, moved.RelativePath, moved.Section).ShouldBe((card.Id, CardState.Approved, @"ОВ\Арматура\Задвижка.rfa", "ОВ"));
    }

    [Test]
    [Arguments(@"_На проверке\ОВ")]
    [Arguments(@"..\ОВ")]
    [Arguments(@".toadflow")]
    [Arguments("")]
    public async Task ApproveNew_BadFolder_Refused(string folder)
    {
        await StartAsync();
        await _library.ScanAsync(File(@"_На проверке\Задвижка.rfa", "z"));

        await ErrorAsync(await _coordinator.PostJsonAsync($"/api/families/{(await CardAsync("Задвижка")).Id}/approve", new ApproveRequest(folder)));
        _library.Agent.FileActions.ShouldBeEmpty();
    }

    [Test]
    public async Task ApproveCandidate_ReplacesCurrentFile_NoUnreviewedIssue()
    {
        await StartAsync();
        await _library.ScanAsync(File(@"ОВ\Клапан.rfa", "a"));
        await _library.ScanAsync(File(@"ОВ\Клапан.rfa", "a"), File(@"_На проверке\Клапан.rfa", "b"));

        (await _coordinator.PostAsync($"/api/families/versions/{await VersionIdAsync("b")}/approve", null)).EnsureSuccessStatusCode();

        var action = _library.Agent.FileActions.Single();
        (action.Kind, action.RelativePath, action.TargetPath, action.Hash, action.ReplaceHash)
            .ShouldBe((FamilyFileActionKind.Move, @"_На проверке\Клапан.rfa", @"ОВ\Клапан.rfa", Hash("b"), (string?)Hash("a")));
        await _library.ReportLastAsync(File(@"ОВ\Клапан.rfa", "b"));
        var card = await _coordinator.GetJsonAsync<FamilyCardView>($"/api/families/{(await CardAsync("Клапан")).Id}");
        card.Versions.Single(v => v.IsCurrent).Hash.ShouldBe(Hash("b"));
        card.Versions.Single(v => v.IsCurrent).State.ShouldBe(VersionState.Approved);
        (await OpenIssuesAsync()).ShouldBeEmpty();
    }

    [Test]
    public async Task RejectCandidate_DeletesFile_VersionRejectedWithReason()
    {
        await StartAsync();
        await _library.ScanAsync(File(@"ОВ\Клапан.rfa", "a"));
        await _library.ScanAsync(File(@"ОВ\Клапан.rfa", "a"), File(@"_На проверке\Клапан.rfa", "b"));
        var candidate = await VersionIdAsync("b");

        await ErrorAsync(await _coordinator.PostJsonAsync($"/api/families/versions/{candidate}/reject", new RejectVersionRequest(" ")));
        (await _coordinator.PostJsonAsync($"/api/families/versions/{candidate}/reject", new RejectVersionRequest("не тот DN"))).EnsureSuccessStatusCode();
        await _library.ReportLastAsync(File(@"ОВ\Клапан.rfa", "a"));

        _library.Agent.FileActions.Single().Kind.ShouldBe(FamilyFileActionKind.Delete);
        var version = await DbAsync(db => db.Set<FamilyVersion>().SingleAsync(v => v.Id == candidate));
        (version.State, version.Note).ShouldBe((VersionState.Rejected, "отклонено: не тот DN"));
    }

    [Test]
    public async Task Archive_ThenRestore_SameCardBack()
    {
        await StartAsync();
        await _library.ScanAsync(File(@"ОВ\Клапан.rfa", "a", "t"));
        var id = (await CardAsync("Клапан")).Id;

        (await _coordinator.PostAsync($"/api/families/{id}/archive", null)).EnsureSuccessStatusCode();
        await _library.ReportLastAsync();
        (await CardAsync("Клапан")).State.ShouldBe(CardState.Archived);
        (await _coordinator.PostAsync($"/api/families/{id}/restore", null)).EnsureSuccessStatusCode();
        await _library.ReportLastAsync(File(@"ОВ\Клапан.rfa", "a", "t"));

        _library.Agent.FileActions.Select(a => (a.Kind, a.RelativePath, a.Hash, a.CatalogHash)).ShouldBe([
            (FamilyFileActionKind.Delete, @"ОВ\Клапан.rfa", Hash("a"), (string?)Hash("t")),
            (FamilyFileActionKind.Restore, @"ОВ\Клапан.rfa", Hash("a"), Hash("t"))
        ]);
        var card = await CardAsync("Клапан");
        (card.Id, card.State).ShouldBe((id, CardState.Approved));
    }

    [Test]
    public async Task Restore_NameTakenMeanwhile_Conflict()
    {
        await StartAsync();
        await _library.ScanAsync(File(@"ОВ\Клапан.rfa", "a"));
        var id = (await CardAsync("Клапан")).Id;
        await _library.ScanAsync();
        await _library.ScanAsync(File(@"КР\Клапан.rfa", "другой", year: 2022));

        var response = await _coordinator.PostAsync($"/api/families/{id}/restore", null);

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
    }

    [Test]
    public async Task Revert_PreviousVersionBecomesCurrent_WithNoteAndAuthor()
    {
        await StartAsync();
        await _library.ScanAsync(File(@"ОВ\Клапан.rfa", "a"));
        await _library.ScanAsync(File(@"ОВ\Клапан.rfa", "b"));
        (await _coordinator.PostAsync($"/api/families/issues/{(await OpenIssuesAsync()).Single().Id}/resolve", null)).EnsureSuccessStatusCode();

        (await _coordinator.PostAsync($"/api/families/versions/{await VersionIdAsync("a")}/revert", null)).EnsureSuccessStatusCode();
        var action = _library.Agent.FileActions.Single();
        (action.Kind, action.RelativePath, action.Hash, action.ReplaceHash).ShouldBe((FamilyFileActionKind.Restore, @"ОВ\Клапан.rfa", Hash("a"), (string?)Hash("b")));
        await _library.ReportLastAsync(File(@"ОВ\Клапан.rfa", "a"));

        var card = await _coordinator.GetJsonAsync<FamilyCardView>($"/api/families/{(await CardAsync("Клапан")).Id}");
        var current = card.Versions.Single(v => v.IsCurrent);
        (current.Hash, current.AuthorName).ShouldBe((Hash("a"), "Тест Operator"));
        current.Note.ShouldStartWith("возврат к версии от ");
        card.Versions.Count.ShouldBe(3);
        (await OpenIssuesAsync()).ShouldBeEmpty();
    }

    [Test]
    public async Task AgentRefuses_ErrorShown_RevertLeavesNoPublication()
    {
        await StartAsync();
        await _library.ScanAsync(File(@"ОВ\Клапан.rfa", "a"));
        await _library.ScanAsync(File(@"ОВ\Клапан.rfa", "b"));
        _library.Agent.FileAction = _ => new FamilyFileActionResult("файл «ОВ\\Клапан.rfa» изменился с последнего обхода — дождитесь обхода и повторите");

        var error = await ErrorAsync(await _coordinator.PostAsync($"/api/families/versions/{await VersionIdAsync("a")}/revert", null));

        error.ShouldContain("Файл «ОВ");
        (await DbAsync(db => db.Set<FamilyPublication>().CountAsync())).ShouldBe(0);
        _library.Agent.ScanRequests.Count.ShouldBe(2); // обход по отказу не просили
    }

    [Test]
    public async Task OldLibraryAgent_ActionsUnavailable()
    {
        await StartAsync([ModuleKinds.Export, ModuleKinds.FamiliesScan]);
        await _library.ScanAsync(File(@"ОВ\Клапан.rfa", "a"));

        var state = await _coordinator.GetJsonAsync<FileActionsState>("/api/families/actions");
        var error = await ErrorAsync(await _coordinator.PostAsync($"/api/families/{(await CardAsync("Клапан")).Id}/archive", null));

        state.Available.ShouldBeFalse();
        state.Reason!.ShouldContain("старой версии");
        error.ShouldContain("старой версии");
        _library.Agent.FileActions.ShouldBeEmpty();
    }

    [Test]
    public async Task Duplicate_Keep_NotAskedAgain()
    {
        await StartAsync();
        await _library.ScanAsync(File(@"ОВ\Клапан.rfa", "a"));
        await _library.ScanAsync(File(@"ОВ\Клапан.rfa", "a"), File(@"ОВ\Клапан копия.rfa", "a"));
        var issue = (await OpenIssuesAsync()).Single();
        issue.Kind.ShouldBe(IssueKind.Duplicate);

        (await _coordinator.PostJsonAsync($"/api/families/issues/{issue.Id}/answer", new IssueAnswer("keep", null))).EnsureSuccessStatusCode();
        await _library.ScanAsync(File(@"ОВ\Клапан.rfa", "a"), File(@"ОВ\Клапан копия.rfa", "a"));

        (await OpenIssuesAsync()).ShouldBeEmpty();
        var answered = await DbAsync(db => db.Set<FamilyIssue>().SingleAsync(i => i.Id == issue.Id));
        (answered.Dismissed, answered.Resolution).ShouldBe((true, "оставлено как есть"));
    }

    [Test]
    public async Task Duplicate_Delete_AgentDeletesCopy()
    {
        await StartAsync();
        await _library.ScanAsync(File(@"ОВ\Клапан.rfa", "a"));
        await _library.ScanAsync(File(@"ОВ\Клапан.rfa", "a"), File(@"ОВ\Клапан копия.rfa", "a"));

        (await _coordinator.PostJsonAsync($"/api/families/issues/{(await OpenIssuesAsync()).Single().Id}/answer", new IssueAnswer("delete", null)))
            .EnsureSuccessStatusCode();

        var action = _library.Agent.FileActions.Single();
        (action.Kind, action.RelativePath, action.Hash).ShouldBe((FamilyFileActionKind.Delete, @"ОВ\Клапан копия.rfa", Hash("a")));
        (await OpenIssuesAsync()).ShouldBeEmpty();
    }

    [Test]
    public async Task NameConflict_Rename_MovesUnderNewName()
    {
        await StartAsync();
        await _library.ScanAsync(File(@"ОВ\Клапан.rfa", "a"));
        await _library.ScanAsync(File(@"ОВ\Клапан.rfa", "a"), File(@"КР\Клапан.rfa", "c"));
        var issue = (await OpenIssuesAsync()).Single();
        issue.Kind.ShouldBe(IssueKind.NameConflict);

        await ErrorAsync(await _coordinator.PostJsonAsync($"/api/families/issues/{issue.Id}/answer", new IssueAnswer("rename", "клапан")));
        await ErrorAsync(await _coordinator.PostJsonAsync($"/api/families/issues/{issue.Id}/answer", new IssueAnswer("rename", "Клапан:КР")));
        (await _coordinator.PostJsonAsync($"/api/families/issues/{issue.Id}/answer", new IssueAnswer("rename", "Клапан КР"))).EnsureSuccessStatusCode();
        await _library.ReportLastAsync(File(@"ОВ\Клапан.rfa", "a"), File(@"КР\Клапан КР.rfa", "c"));

        _library.Agent.FileActions.Single().TargetPath.ShouldBe(@"КР\Клапан КР.rfa");
        (await CardAsync("Клапан КР")).State.ShouldBe(CardState.Approved);
        (await OpenIssuesAsync()).ShouldBeEmpty();
    }

    [Test]
    public async Task Ambiguous_Same_FileBecomesNewVersion_NextScanQuiet()
    {
        await StartAsync();
        await _library.ScanAsync(File(@"ОВ\Клапан.rfa", "a"));
        var id = (await CardAsync("Клапан")).Id;
        await _library.ScanAsync(File(@"ОВ\Клапан обратный.rfa", "b"));
        var issue = (await OpenIssuesAsync()).Single();
        issue.Kind.ShouldBe(IssueKind.Ambiguous);

        (await _coordinator.PostJsonAsync($"/api/families/issues/{issue.Id}/answer", new IssueAnswer("same", null))).EnsureSuccessStatusCode();
        await _library.ScanAsync(File(@"ОВ\Клапан обратный.rfa", "b"));

        var card = await _coordinator.GetJsonAsync<FamilyCardView>($"/api/families/{id}");
        (card.Name, card.RelativePath, card.State).ShouldBe(("Клапан обратный", @"ОВ\Клапан обратный.rfa", CardState.Approved));
        card.Versions.Select(v => v.Hash).ShouldBe([Hash("b"), Hash("a")]);
        (await OpenIssuesAsync()).ShouldBeEmpty();
        (await DbAsync(db => db.Set<FamilyCard>().CountAsync())).ShouldBe(1);
    }

    [Test]
    public async Task Ambiguous_Other_OldCardArchived_NewCardCreated()
    {
        await StartAsync();
        await _library.ScanAsync(File(@"ОВ\Клапан.rfa", "a"));
        var id = (await CardAsync("Клапан")).Id;
        await _library.ScanAsync(File(@"ОВ\Кран.rfa", "b"));

        (await _coordinator.PostJsonAsync($"/api/families/issues/{(await OpenIssuesAsync()).Single().Id}/answer", new IssueAnswer("other", null)))
            .EnsureSuccessStatusCode();
        await _library.ScanAsync(File(@"ОВ\Кран.rfa", "b"));

        (await DbAsync(db => db.Set<FamilyCard>().SingleAsync(c => c.Id == id))).State.ShouldBe(CardState.Archived);
        var crane = await CardAsync("Кран");
        (crane.State, crane.RelativePath).ShouldBe((CardState.Approved, @"ОВ\Кран.rfa"));
        (await OpenIssuesAsync()).ShouldBeEmpty();
    }

    [Test]
    public async Task Unreadable_Retry_VersionWaitsForFarmAgain()
    {
        await StartAsync();
        await _library.ScanAsync(File(@"ОВ\Клапан.rfa", "a"));
        var card = await CardAsync("Клапан");
        var issueId = Guid.CreateVersion7();
        await DbAsync(async db =>
        {
            var version = await db.Set<FamilyVersion>().SingleAsync(v => v.CardId == card.Id);
            version.ReadState = ReadState.Failed;
            version.ReadError = "Revit не открыл файл";
            db.Set<FamilyIssue>().Add(new FamilyIssue
            {
                Id = issueId, Kind = IssueKind.Unreadable, CardId = card.Id, RelativePath = card.RelativePath, Hash = Hash("a"), Text = "x",
                CreatedAt = DateTimeOffset.UtcNow
            });
            await db.SaveChangesAsync();
        });

        (await _coordinator.PostJsonAsync($"/api/families/issues/{issueId}/answer", new IssueAnswer("retry", null))).EnsureSuccessStatusCode();

        var retried = await DbAsync(db => db.Set<FamilyVersion>().SingleAsync(v => v.CardId == card.Id));
        (retried.ReadState, retried.ReadError).ShouldBe((ReadState.Pending, (string?)null));
        await ErrorAsync(await _coordinator.PostJsonAsync($"/api/families/issues/{issueId}/answer", new IssueAnswer("retry", null)));
    }

    [Test]
    public async Task Folders_ListApprovedFoldersWithParents()
    {
        await StartAsync();
        await _library.ScanAsync(File(@"ОВ\Клапаны\Клапан.rfa", "a"), File(@"КР\Арматура.rfa", "b"), File(@"_На проверке\Новое\Новое.rfa", "c"));

        var folders = await _coordinator.GetJsonAsync<List<string>>("/api/families/folders");

        folders.ShouldBe(["КР", "ОВ", @"ОВ\Клапаны"]);
    }

    [Test]
    public async Task Viewer_CannotAct()
    {
        await StartAsync();
        await _library.ScanAsync(File(@"ОВ\Клапан.rfa", "a"));

        (await (await ClientAsync(UserRole.Viewer)).PostAsync($"/api/families/{(await CardAsync("Клапан")).Id}/archive", null)).StatusCode
            .ShouldBe(HttpStatusCode.Forbidden);
    }
}
