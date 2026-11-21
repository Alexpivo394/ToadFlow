using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using ToadFlow.Contracts;
using ToadFlow.Server.Families.Cards;
using ToadFlow.Server.Families.Data;
using ToadFlow.Server.Families.Panel;
using ToadFlow.Server.Families.Search;
using ToadFlow.Server.Runs;
using ToadFlow.Server.Tests.Infrastructure;
using static ToadFlow.Server.Tests.Infrastructure.FamiliesFixtures;

namespace ToadFlow.Server.Tests;

/// <summary>Маршруты панели «Семейства» в Revit: библиотека, публикация, загрузки, состояние семейств проекта, описание версии.</summary>
public class FamiliesPanelTests : ServerTest
{
    public FamiliesPanelTests() => Services = WithoutScanLoop;

    private FamilyLibrary _library = null!;

    [After(Test)]
    public async Task StopAgentAsync()
    {
        if (_library is not null) await _library.DisposeAsync();
    }

    private async Task StartLibraryAsync() => _library = await FamilyLibrary.StartAsync(Server, await AdminAsync());

    private async Task<Guid> CardIdAsync(string name) => await DbAsync(db => db.Set<FamilyCard>().Where(c => c.Name == name).Select(c => c.Id).SingleAsync());

    private static PublishRequest Publication(string relativePath, string content, Guid? cardId = null, string? comment = null, Guid? requestId = null) =>
        new($@"{Root}\{relativePath}", Hash(content), cardId, comment, requestId);

    [Test]
    public async Task Library_TellsRootAndWhoCanPublish()
    {
        await StartLibraryAsync();

        var designer = await (await ClientAsync(UserRole.Designer)).GetJsonAsync<LibraryInfo>("/api/families/library");
        var coordinator = await (await ClientAsync(UserRole.Operator)).GetJsonAsync<LibraryInfo>("/api/families/library");

        (designer.Configured, designer.Root, designer.ReviewFolder, designer.CanPublish).ShouldBe((true, Root, "_На проверке", false));
        coordinator.CanPublish.ShouldBeTrue();
    }

    [Test]
    public async Task Publish_NewFamily_ScanGivesVersionAuthorAndComment()
    {
        await StartLibraryAsync();
        var coordinator = await ClientAsync(UserRole.Operator);

        var response = await coordinator.PostJsonAsync("/api/families/publications", Publication(@"_На проверке\Задвижка.rfa", "z", comment: "по каталогу Ду50"));
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var published = await response.ReadJsonAsync<PublishResult>();
        await _library.ReportLastAsync(File(@"_На проверке\Задвижка.rfa", "z"));

        published.ScanStarted.ShouldBeTrue();
        published.RelativePath.ShouldBe(@"_На проверке\Задвижка.rfa");
        var card = await coordinator.GetJsonAsync<FamilyCardView>($"/api/families/{await CardIdAsync("Задвижка")}");
        card.Versions.Single().AuthorName.ShouldBe("Тест Operator");
        card.Versions.Single().Comment.ShouldBe("по каталогу Ду50");
    }

    [Test]
    public async Task Publish_NewVersionOfCard_BecomesCandidateWithComment()
    {
        await StartLibraryAsync();
        await _library.ScanAsync(File(@"ОВ\Клапан.rfa", "a"));
        var id = await CardIdAsync("Клапан");
        var coordinator = await ClientAsync(UserRole.Operator);

        (await coordinator.PostJsonAsync("/api/families/publications", Publication(@"_На проверке\Клапан.rfa", "b", id, "добавлен DN100")))
            .StatusCode.ShouldBe(HttpStatusCode.OK);
        await _library.ReportLastAsync(File(@"ОВ\Клапан.rfa", "a"), File(@"_На проверке\Клапан.rfa", "b"));

        var card = await coordinator.GetJsonAsync<FamilyCardView>($"/api/families/{id}");
        var candidate = card.Versions.Single(v => v.Hash == Hash("b"));
        (candidate.State, candidate.Comment, candidate.IsCurrent).ShouldBe((VersionState.InReview, "добавлен DN100", false));
    }

    [Test]
    public async Task Publish_NameOfAnotherCard_Conflict()
    {
        await StartLibraryAsync();
        await _library.ScanAsync(File(@"ОВ\Клапан.rfa", "a"));

        var response = await (await ClientAsync(UserRole.Operator)).PostJsonAsync("/api/families/publications", Publication(@"_На проверке\клапан.rfa", "b"));

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await response.Content.ReadAsStringAsync()).ShouldContain("уже занято");
    }

    [Test]
    public async Task Publish_VersionUnderOtherName_BadRequest()
    {
        await StartLibraryAsync();
        await _library.ScanAsync(File(@"ОВ\Клапан.rfa", "a"));

        var response = await (await ClientAsync(UserRole.Operator)).PostJsonAsync("/api/families/publications",
            Publication(@"_На проверке\Клапан2.rfa", "b", await CardIdAsync("Клапан")));

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Test]
    [Arguments(@"\\other\Families\ОВ\Новое.rfa")]
    [Arguments(@"\\nas\Families\..\Новое.rfa")]
    [Arguments(@"\\nas\Families\.toadflow\history\x.rfa")]
    [Arguments(@"\\nas\Families\ОВ\Новое.txt")]
    public async Task Publish_PathOutsideLibrary_BadRequest(string path)
    {
        await StartLibraryAsync();

        var response = await (await ClientAsync(UserRole.Operator)).PostJsonAsync("/api/families/publications", new PublishRequest(path, Hash("n"), null, null, null));

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Test]
    public async Task Publish_OverApprovedFile_StaysUnreviewedChange()
    {
        await StartLibraryAsync();
        await _library.ScanAsync(File(@"ОВ\Клапан.rfa", "a"));
        var id = await CardIdAsync("Клапан");
        var coordinator = await ClientAsync(UserRole.Operator);

        (await coordinator.PostJsonAsync("/api/families/publications", Publication(@"ОВ\Клапан.rfa", "b", id, "мимо проверки"))).EnsureSuccessStatusCode();
        await _library.ReportLastAsync(File(@"ОВ\Клапан.rfa", "b"));

        var current = (await coordinator.GetJsonAsync<FamilyCardView>($"/api/families/{id}")).Versions.Single(v => v.IsCurrent);
        (current.Note, current.Comment).ShouldBe(("изменено без проверки", "мимо проверки"));
        (await DbAsync(db => db.Set<FamilyIssue>().CountAsync(i => i.Kind == IssueKind.UnreviewedChange && i.ResolvedAt == null))).ShouldBe(1);
    }

    [Test]
    public async Task Publish_Designer_Forbidden()
    {
        await StartLibraryAsync();

        var response = await (await ClientAsync(UserRole.Designer)).PostJsonAsync("/api/families/publications", Publication(@"ОВ\Новое.rfa", "n"));

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Test]
    public async Task Publish_WhileScanRunning_ScanStartsRightAfter()
    {
        await StartLibraryAsync();
        var running = await _library.RequestScanAsync();

        var published = await (await (await ClientAsync(UserRole.Operator)).PostJsonAsync("/api/families/publications", Publication(@"ОВ\Новое.rfa", "n")))
            .ReadJsonAsync<PublishResult>();
        (await _library.ReportAsync(new(running, true, [], [], []))).EnsureSuccessStatusCode();
        await using var scope = Server.Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<ToadFlow.Server.Families.Scan.LibraryScanner>().TickAsync(CancellationToken.None);

        published.ScanStarted.ShouldBeFalse();
        _library.Agent.ScanRequests.Count.ShouldBe(2);
    }

    [Test]
    public async Task Loads_CountedAndRaiseInSearch()
    {
        var now = DateTimeOffset.UtcNow;
        var older = await DbAsync(db => SeedCardAsync(db, "Клапан старый", @"ОВ\Клапан старый.rfa", CardState.Approved, null, now.AddDays(-2)));
        await DbAsync(db => SeedCardAsync(db, "Клапан новый", @"ОВ\Клапан новый.rfa", CardState.Approved, null, now));
        var designer = await ClientAsync(UserRole.Designer);

        (await designer.PostJsonAsync($"/api/families/{older}/loads", new LoadReport(Hash(@"ОВ\Клапан старый.rfa"), "Дом 1.rvt"))).EnsureSuccessStatusCode();
        (await designer.PostJsonAsync($"/api/families/{older}/loads", new LoadReport(null, "Дом 2.rvt"))).EnsureSuccessStatusCode();

        var page = await designer.GetJsonAsync<Page<FamilyHit>>("/api/families/search?q=клапан");
        page.Items.Select(h => (h.Name, h.LoadCount)).ShouldBe([("Клапан старый", 2), ("Клапан новый", 0)]);
        var card = await designer.GetJsonAsync<FamilyCardView>($"/api/families/{older}");
        card.LoadCount.ShouldBe(2);
        card.LastLoadedAt.ShouldNotBeNull();
        var loads = await DbAsync(db => db.Set<FamilyLoad>().OrderBy(l => l.At).ToListAsync());
        loads.Select(l => (l.Project, l.VersionId is not null)).ShouldBe([("Дом 1.rvt", true), ("Дом 2.rvt", false)]);
    }

    [Test]
    public async Task Loads_CardHiddenFromDesigner_NotFound()
    {
        var archived = await DbAsync(db => SeedCardAsync(db, "Кран", @"ОВ\Кран.rfa", CardState.Archived, null, DateTimeOffset.UtcNow));

        (await (await ClientAsync(UserRole.Designer)).PostJsonAsync($"/api/families/{archived}/loads", new LoadReport(null, null)))
            .StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Test]
    public async Task ProjectStatus_TellsCurrentOutdatedArchivedAndSuggestsByName()
    {
        var now = DateTimeOffset.UtcNow;
        var current = await DbAsync(db => SeedCardAsync(db, "Клапан", @"ОВ\Клапан.rfa", CardState.Approved, null, now));
        var archived = await DbAsync(db => SeedCardAsync(db, "Кран", @"ОВ\Кран.rfa", CardState.Archived, null, now));
        var named = await DbAsync(db => SeedCardAsync(db, "Задвижка", @"ОВ\Задвижка.rfa", CardState.Approved, null, now));

        var result = await (await (await ClientAsync(UserRole.Designer)).PostJsonAsync("/api/families/project-status", new ProjectStatusRequest([
            new ProjectFamilyDto("Клапан", current, Hash(@"ОВ\Клапан.rfa")),
            new ProjectFamilyDto("Клапан (старый)", current, Hash("прежний")),
            new ProjectFamilyDto("Кран", archived, Hash(@"ОВ\Кран.rfa")),
            new ProjectFamilyDto("задвижка", null, null),
            new ProjectFamilyDto("Своё", null, null)
        ], null))).ReadJsonAsync<List<ProjectFamilyStatus>>();

        result.Select(r => (r.Name, r.State, r.CardId)).ShouldBe([
            ("Клапан", ProjectFamilyState.Current, (Guid?)current),
            ("Клапан (старый)", ProjectFamilyState.Outdated, current),
            ("Кран", ProjectFamilyState.Archived, archived),
            ("задвижка", ProjectFamilyState.NotFromLibrary, named),
            ("Своё", ProjectFamilyState.NotFromLibrary, null)
        ]);
        result[1].CurrentHash.ShouldBe(Hash(@"ОВ\Клапан.rfa"));
        result[1].FullPath.ShouldBe(@"ОВ\Клапан.rfa"); // корень не задан — путь от корня
    }

    [Test]
    public async Task VersionComment_CoordinatorWritesAndBecomesAuthor()
    {
        var id = await DbAsync(db => SeedCardAsync(db, "Клапан", @"ОВ\Клапан.rfa", CardState.Approved, null, DateTimeOffset.UtcNow));
        var versionId = await DbAsync(db => db.Set<FamilyVersion>().Where(v => v.CardId == id).Select(v => v.Id).SingleAsync());
        var coordinator = await ClientAsync(UserRole.Operator);

        (await coordinator.PutJsonAsync($"/api/families/versions/{versionId}/comment", new VersionCommentRequest("уточнён DN", true))).EnsureSuccessStatusCode();
        (await (await ClientAsync(UserRole.Viewer)).PutJsonAsync($"/api/families/versions/{versionId}/comment", new VersionCommentRequest("x", true)))
            .StatusCode.ShouldBe(HttpStatusCode.Forbidden);

        var version = (await coordinator.GetJsonAsync<FamilyCardView>($"/api/families/{id}")).Versions.Single();
        (version.Comment, version.AuthorName).ShouldBe(("уточнён DN", "Тест Operator"));
    }
}
