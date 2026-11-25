using Microsoft.EntityFrameworkCore;
using Shouldly;
using ToadFlow.Contracts;
using ToadFlow.Server.Families.Data;
using ToadFlow.Server.Families.Panel;
using ToadFlow.Server.Tests.Infrastructure;
using static ToadFlow.Server.Tests.Infrastructure.FamiliesFixtures;

namespace ToadFlow.Server.Tests;

/// <summary>Файлы папки итераций: <c>_Итерации\семейство\итерация\семейство.rfa</c>.</summary>
public class FamiliesIterationScanTests : ServerTest
{
    private const string Base = @"АР\Двери\Дверь.rfa";
    private const string Polk = @"_Итерации\Дверь\ЖК Полковая\Дверь.rfa";

    public FamiliesIterationScanTests() => Services = WithoutScanLoop;

    private FamilyLibrary _library = null!;

    [After(Test)]
    public async Task StopAgentAsync()
    {
        if (_library is not null) await _library.DisposeAsync();
    }

    private async Task<FamilyLibrary> LibraryAsync() => _library = await FamilyLibrary.StartAsync(Server, await AdminAsync());

    private Task<List<FamilyCard>> CardsAsync() => DbAsync(db => db.Set<FamilyCard>().AsNoTracking().ToListAsync());

    private Task<List<FamilyIteration>> IterationsAsync() => DbAsync(db => db.Set<FamilyIteration>().AsNoTracking().OrderBy(i => i.CreatedAt).ToListAsync());

    private Task<List<FamilyVersion>> VersionsAsync() =>
        DbAsync(db => db.Set<FamilyVersion>().AsNoTracking().OrderBy(v => v.SeenAt).ThenBy(v => v.Id).ToListAsync());

    private Task<List<FamilyIssue>> OpenIssuesAsync() => DbAsync(db => db.Set<FamilyIssue>().AsNoTracking().Where(i => i.ResolvedAt == null).ToListAsync());

    [Test]
    public async Task NewIterationFile_CreatesIterationAndVersion_NotDuplicate()
    {
        var library = await LibraryAsync();
        await library.ScanAsync(File(Base, "a"));
        var baseVersion = (await VersionsAsync()).ShouldHaveSingleItem();

        await library.ScanAsync(File(Base, "a"), File(Polk, "a"));

        var card = (await CardsAsync()).ShouldHaveSingleItem();
        card.CurrentVersionId.ShouldBe(baseVersion.Id);
        var iteration = (await IterationsAsync()).ShouldHaveSingleItem();
        (iteration.CardId, iteration.Name, iteration.RelativePath, iteration.RemovedAt).ShouldBe((card.Id, "ЖК Полковая", Polk, null));
        var versions = await VersionsAsync();
        versions.Count.ShouldBe(2);
        (versions[1].IterationId, versions[1].Hash, versions[1].ReadState, versions[1].RelativePath).ShouldBe((iteration.Id, Hash("a"), ReadState.Pending, Polk));
        iteration.CurrentVersionId.ShouldBe(versions[1].Id);
        (await OpenIssuesAsync()).ShouldBeEmpty();
    }

    [Test]
    public async Task IterationFileChanged_NewIterationVersion_BaseUntouched()
    {
        var library = await LibraryAsync();
        await library.ScanAsync(File(Base, "a"), File(Polk, "a"));

        await library.ScanAsync(File(Base, "a"), File(Polk, "b", catalog: "типы"));

        var card = (await CardsAsync()).ShouldHaveSingleItem();
        var iteration = (await IterationsAsync()).ShouldHaveSingleItem();
        var versions = await VersionsAsync();
        versions.Count.ShouldBe(3);
        versions.Count(v => v.IterationId is null).ShouldBe(1);
        card.CurrentVersionId.ShouldBe(versions.Single(v => v.IterationId is null).Id);
        var latest = versions.Single(v => v.Id == iteration.CurrentVersionId);
        (latest.Hash, latest.CatalogHash, latest.Note).ShouldBe((Hash("b"), Hash("типы"), ""));
        (await OpenIssuesAsync()).ShouldBeEmpty();
    }

    [Test]
    public async Task FinalIterationChanged_IssueChangedAfterFinal()
    {
        var library = await LibraryAsync();
        await library.ScanAsync(File(Base, "a"), File(Polk, "a"));
        await DbAsync(db => db.Set<FamilyIteration>().ExecuteUpdateAsync(i => i.SetProperty(x => x.FinalVersionId, x => x.CurrentVersionId).SetProperty(x => x.FinalComment, "сдано")));

        await library.ScanAsync(File(Base, "a"), File(Polk, "b"));

        var iteration = (await IterationsAsync()).ShouldHaveSingleItem();
        iteration.FinalVersionId.ShouldNotBe(iteration.CurrentVersionId);
        var issue = (await OpenIssuesAsync()).ShouldHaveSingleItem();
        (issue.Kind, issue.RelativePath, issue.Hash).ShouldBe((IssueKind.ChangedAfterFinal, Polk, Hash("b")));
    }

    [Test]
    public async Task IterationFolderRenamed_SameIteration()
    {
        var library = await LibraryAsync();
        await library.ScanAsync(File(Base, "a"), File(Polk, "b"));

        const string renamed = @"_Итерации\Дверь\Полковая К2\Дверь.rfa";
        await library.ScanAsync(File(Base, "a"), File(renamed, "b"));

        var iteration = (await IterationsAsync()).ShouldHaveSingleItem();
        (iteration.Name, iteration.RelativePath, iteration.RemovedAt).ShouldBe(("Полковая К2", renamed, null));
        (await VersionsAsync()).Count.ShouldBe(2);
    }

    [Test]
    public async Task IterationFileGone_IncompleteScan_NotRemoved()
    {
        var library = await LibraryAsync();
        await library.ScanAsync(File(Base, "a"), File(Polk, "a"));

        await library.ScanAsync([File(Base, "a")], complete: false);

        (await IterationsAsync()).ShouldHaveSingleItem().RemovedAt.ShouldBeNull();
    }

    [Test]
    public async Task IterationFileGone_CompleteScan_Removed_AndBackLater()
    {
        var library = await LibraryAsync();
        await library.ScanAsync(File(Base, "a"), File(Polk, "a"));

        await library.ScanAsync(File(Base, "a"));
        (await IterationsAsync()).ShouldHaveSingleItem().RemovedAt.ShouldNotBeNull();

        await library.ScanAsync(File(Base, "a"), File(Polk, "a"));
        (await IterationsAsync()).ShouldHaveSingleItem().RemovedAt.ShouldBeNull();
        (await VersionsAsync()).Count.ShouldBe(2);
    }

    [Test]
    [Arguments(@"_Итерации\Дверь\Дверь.rfa")]
    [Arguments(@"_Итерации\Дверь\ЖК Полковая\Дверь другая.rfa")]
    [Arguments(@"_Итерации\Окно\ЖК Полковая\Окно.rfa")]
    [Arguments(@"_Итерации\Дверь\ЖК Полковая\Ещё\Дверь.rfa")]
    public async Task MisplacedIterationFile_Issue_NoCard(string path)
    {
        var library = await LibraryAsync();
        await library.ScanAsync(File(Base, "a"));

        await library.ScanAsync(File(Base, "a"), File(path, "x"));

        (await CardsAsync()).ShouldHaveSingleItem();
        (await IterationsAsync()).ShouldBeEmpty();
        var issue = (await OpenIssuesAsync()).ShouldHaveSingleItem();
        (issue.Kind, issue.RelativePath).ShouldBe((IssueKind.MisplacedIteration, path));

        await library.ScanAsync(File(Base, "a"));
        (await OpenIssuesAsync()).ShouldBeEmpty();
    }

    [Test]
    public async Task Publication_NewIteration_SetsCommentSilentFinal()
    {
        var library = await LibraryAsync();
        await library.ScanAsync(File(Base, "a"));
        var cardId = (await CardsAsync()).Single().Id;

        var response = await library.Admin.PostJsonAsync("/api/families/publications",
            new PublishRequest($@"{Root}\{Polk}", Hash("p"), cardId, "дверь под Полковую", null)
            {
                IterationName = "ЖК Полковая", IterationComment = "для корпуса 1", Silent = true, FinalComment = "сдано заказчику"
            });
        response.EnsureSuccessStatusCode();
        await library.ReportLastAsync(File(Base, "a"), File(Polk, "p"));

        var iteration = (await IterationsAsync()).ShouldHaveSingleItem();
        var version = (await VersionsAsync()).Single(v => v.IterationId == iteration.Id);
        (iteration.Comment, iteration.FinalVersionId, iteration.FinalComment, iteration.FinalByUserId).ShouldBe(("для корпуса 1", version.Id, "сдано заказчику", version.AuthorUserId));
        (version.Comment, version.Silent).ShouldBe(("дверь под Полковую", true));
        version.AuthorUserId.ShouldNotBeNull();
    }

    [Test]
    public async Task Publication_ToExistingIteration_NewVersionWithComment()
    {
        var library = await LibraryAsync();
        await library.ScanAsync(File(Base, "a"), File(Polk, "a"));
        var iteration = (await IterationsAsync()).Single();

        var response = await library.Admin.PostJsonAsync("/api/families/publications",
            new PublishRequest($@"{Root}\{Polk}", Hash("q"), iteration.CardId, "ручка слева", null) { IterationId = iteration.Id });
        response.EnsureSuccessStatusCode();
        await library.ReportLastAsync(File(Base, "a"), File(Polk, "q"));

        var currentId = (await IterationsAsync()).Single().CurrentVersionId;
        var current = (await VersionsAsync()).Single(v => v.Id == currentId);
        (current.Hash, current.Comment, current.Silent).ShouldBe((Hash("q"), "ручка слева", false));
    }
}
