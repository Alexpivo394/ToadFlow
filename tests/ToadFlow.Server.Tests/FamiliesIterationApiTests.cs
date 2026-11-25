using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Shouldly;
using ToadFlow.Contracts;
using ToadFlow.Contracts.Families;
using ToadFlow.Server.Families.Cards;
using ToadFlow.Server.Families.Data;
using ToadFlow.Server.Families.Panel;
using ToadFlow.Server.Families.Search;
using ToadFlow.Server.Runs;
using ToadFlow.Server.Tests.Infrastructure;
using static ToadFlow.Server.Tests.Infrastructure.FamiliesFixtures;

namespace ToadFlow.Server.Tests;

/// <summary>Итерации, финал и флаги семейства через API веба и панели.</summary>
public class FamiliesIterationApiTests : ServerTest
{
    private const string Base = @"АР\Двери\Дверь.rfa";
    private const string Polk = @"_Итерации\Дверь\ЖК Полковая\Дверь.rfa";

    public FamiliesIterationApiTests() => Services = WithoutScanLoop;

    private FamilyLibrary _library = null!;
    private HttpClient _coordinator = null!;

    [After(Test)]
    public async Task StopAgentAsync()
    {
        if (_library is not null) await _library.DisposeAsync();
    }

    /// <summary>Библиотека с дверью и её итерацией «ЖК Полковая» (содержимое «a» у обеих).</summary>
    private async Task<(Guid Card, Guid Iteration)> StartAsync()
    {
        _library = await FamilyLibrary.StartAsync(Server, await AdminAsync());
        _coordinator = await ClientAsync(UserRole.Operator);
        await _library.ScanAsync(File(Base, "a"), File(Polk, "a"));
        var card = await DbAsync(db => db.Set<FamilyCard>().Select(c => c.Id).SingleAsync());
        var iteration = await DbAsync(db => db.Set<FamilyIteration>().Select(i => i.Id).SingleAsync());
        return (card, iteration);
    }

    private static async Task<string> ErrorAsync(HttpResponseMessage response, HttpStatusCode status = HttpStatusCode.BadRequest)
    {
        response.StatusCode.ShouldBe(status);
        return await response.Content.ReadAsStringAsync();
    }

    [Test]
    public async Task Card_ShowsIterationsWithContentAndVersions()
    {
        var (card, iteration) = await StartAsync();
        await _library.ScanAsync(File(Base, "a"), File(Polk, "b"));

        var view = await _coordinator.GetJsonAsync<FamilyCardView>($"/api/families/{card}");

        var shown = view.Iterations.ShouldHaveSingleItem();
        (shown.Id, shown.Name, shown.FullPath, shown.CurrentHash, shown.IsFinal).ShouldBe((iteration, "ЖК Полковая", $@"{Root}\{Polk}", Hash("b"), false));
        view.Versions.Count(v => v.IterationId == iteration).ShouldBe(2);
        view.Versions.Single(v => v.IsCurrent).IterationId.ShouldBeNull();
    }

    [Test]
    public async Task CreateIteration_RestoresChosenVersionIntoIterationFolder()
    {
        var (card, _) = await StartAsync();
        var version = await DbAsync(db => db.Set<FamilyVersion>().Where(v => v.IterationId == null).Select(v => v.Id).SingleAsync());

        (await _coordinator.PostJsonAsync($"/api/families/{card}/iterations", new NewIterationRequest("Север К2", version, "дверь под Север", true)))
            .EnsureSuccessStatusCode();

        var action = _library.Agent.FileActions.Single();
        (action.Kind, action.RelativePath, action.Hash, action.ReplaceHash).ShouldBe((FamilyFileActionKind.Restore, @"_Итерации\Дверь\Север К2\Дверь.rfa", Hash("a"), (string?)null));
        await _library.ReportLastAsync(File(Base, "a"), File(Polk, "a"), File(@"_Итерации\Дверь\Север К2\Дверь.rfa", "a"));
        var created = await DbAsync(db => db.Set<FamilyIteration>().AsNoTracking().SingleAsync(i => i.Name == "Север К2"));
        (created.Comment, created.CoordinatorsOnly, created.CurrentVersionId).ShouldBe(("дверь под Север", true, created.CurrentVersionId));
        var head = await DbAsync(db => db.Set<FamilyVersion>().AsNoTracking().SingleAsync(v => v.Id == created.CurrentVersionId));
        head.Note.ShouldStartWith("итерация от версии от ");
        (await DbAsync(db => db.Set<FamilyIssue>().CountAsync())).ShouldBe(0);
    }

    [Test]
    public async Task CreateIteration_NameTaken_409_BadName_400()
    {
        var (card, _) = await StartAsync();
        var version = await DbAsync(db => db.Set<FamilyVersion>().Select(v => v.Id).FirstAsync());

        (await ErrorAsync(await _coordinator.PostJsonAsync($"/api/families/{card}/iterations", new NewIterationRequest("жк полковая", version, null, false)),
            HttpStatusCode.Conflict)).ShouldContain("уже есть");
        (await ErrorAsync(await _coordinator.PostJsonAsync($"/api/families/{card}/iterations", new NewIterationRequest(@"а\б", version, null, false))))
            .ShouldContain("имя папки");
        _library.Agent.FileActions.ShouldBeEmpty();
    }

    [Test]
    public async Task Final_RequiresComment_ThenUnfinal()
    {
        var (_, iteration) = await StartAsync();

        (await ErrorAsync(await _coordinator.PostJsonAsync($"/api/families/iterations/{iteration}/final", new IterationFinalRequest(null, "  "))))
            .ShouldContain("что проделано");
        (await _coordinator.PostJsonAsync($"/api/families/iterations/{iteration}/final", new IterationFinalRequest(null, "сдано, корпус 1"))).EnsureSuccessStatusCode();
        var final = await DbAsync(db => db.Set<FamilyIteration>().AsNoTracking().SingleAsync());
        (final.FinalVersionId, final.FinalComment).ShouldBe((final.CurrentVersionId, "сдано, корпус 1"));

        (await _coordinator.DeleteAsync($"/api/families/iterations/{iteration}/final")).EnsureSuccessStatusCode();
        (await DbAsync(db => db.Set<FamilyIteration>().AsNoTracking().SingleAsync())).FinalVersionId.ShouldBeNull();
    }

    [Test]
    public async Task PublishIntoFinalIteration_Refused()
    {
        var (card, iteration) = await StartAsync();
        (await _coordinator.PostJsonAsync($"/api/families/iterations/{iteration}/final", new IterationFinalRequest(null, "сдано"))).EnsureSuccessStatusCode();

        var response = await _coordinator.PostJsonAsync("/api/families/publications",
            new PublishRequest($@"{Root}\{Polk}", Hash("x"), card, null, null) { IterationId = iteration });

        (await ErrorAsync(response)).ShouldContain("завершена");
    }

    [Test]
    public async Task PublishIntoIteration_WrongPathOrBaseIntoIterationsFolder_Refused()
    {
        var (card, iteration) = await StartAsync();

        (await ErrorAsync(await _coordinator.PostJsonAsync("/api/families/publications",
            new PublishRequest($@"{Root}\{Base}", Hash("x"), card, null, null) { IterationId = iteration }))).ShouldContain("Файл итерации лежит");
        (await ErrorAsync(await _coordinator.PostJsonAsync("/api/families/publications",
            new PublishRequest($@"{Root}\{Polk}", Hash("x"), card, null, null)))).ShouldContain("только итерация");
        (await ErrorAsync(await _coordinator.PostJsonAsync("/api/families/publications",
            new PublishRequest($@"{Root}\{Base}", Hash("x"), card, null, null) { FinalComment = "финал" }))).ShouldContain("Финальной");
    }

    [Test]
    public async Task Revert_InIteration_RestoresOnIterationPath()
    {
        var (_, iteration) = await StartAsync();
        await _library.ScanAsync(File(Base, "a"), File(Polk, "b"));
        var first = await DbAsync(db => db.Set<FamilyVersion>().Where(v => v.IterationId == iteration && v.Hash == Hash("a")).Select(v => v.Id).SingleAsync());

        (await _coordinator.PostAsync($"/api/families/versions/{first}/revert", null)).EnsureSuccessStatusCode();

        var action = _library.Agent.FileActions.Single();
        (action.Kind, action.RelativePath, action.Hash, action.ReplaceHash).ShouldBe((FamilyFileActionKind.Restore, Polk, Hash("a"), Hash("b")));
    }

    [Test]
    public async Task Flags_CoordinatorsOnly_DesignerSeesLockWithoutPath()
    {
        var (card, iteration) = await StartAsync();
        (await _coordinator.PutJsonAsync($"/api/families/{card}/flags", new FamilyFlagsRequest(true, true))).EnsureSuccessStatusCode();
        var designer = await ClientAsync(UserRole.Designer);

        var view = await designer.GetJsonAsync<FamilyCardView>($"/api/families/{card}");
        (view.CoordinatorsOnly, view.Silent, view.FullPath).ShouldBe((true, true, ""));
        view.Iterations.Single().FullPath.ShouldBe("");
        (await designer.GetJsonAsync<Page<FamilyHit>>("/api/families/search")).Items.Single().CoordinatorsOnly.ShouldBeTrue();
        (await _coordinator.GetJsonAsync<FamilyCardView>($"/api/families/{card}")).FullPath.ShouldBe($@"{Root}\{Base}");

        var status = await designer.PostJsonAsync("/api/families/project-status",
            new ProjectStatusRequest([new ProjectFamilyDto("Дверь", card, Hash("a")) { IterationId = iteration }], false));
        var row = (await status.Content.ReadFromJsonAsync<List<ProjectFamilyStatus>>(TestJson.Options))!.Single();
        (row.CoordinatorsOnly, row.FullPath).ShouldBe((true, (string?)null));
    }

    [Test]
    public async Task IterationCoordinatorsOnly_OnlyThatIterationLocked()
    {
        var (card, iteration) = await StartAsync();
        (await _coordinator.PutJsonAsync($"/api/families/iterations/{iteration}", new IterationUpdateRequest("для Полковой", true))).EnsureSuccessStatusCode();

        var view = await (await ClientAsync(UserRole.Designer)).GetJsonAsync<FamilyCardView>($"/api/families/{card}");

        view.FullPath.ShouldBe($@"{Root}\{Base}");
        var shown = view.Iterations.Single();
        (shown.CoordinatorsOnly, shown.FullPath, shown.Comment).ShouldBe((true, "", "для Полковой"));
    }

    [Test]
    public async Task ProjectStatus_ComparesWithOwnIteration_OldStampAsBase()
    {
        var (card, iteration) = await StartAsync();
        await _library.ScanAsync(File(Base, "a"), File(Polk, "b"));
        var designer = await ClientAsync(UserRole.Designer);

        var response = await designer.PostJsonAsync("/api/families/project-status", new ProjectStatusRequest(
        [
            new ProjectFamilyDto("Дверь", card, Hash("a")) { IterationId = iteration },
            new ProjectFamilyDto("Дверь", card, Hash("a"))
        ], false));
        var rows = (await response.Content.ReadFromJsonAsync<List<ProjectFamilyStatus>>(TestJson.Options))!;

        (rows[0].State, rows[0].IterationName, rows[0].CurrentHash, rows[0].FullPath).ShouldBe((ProjectFamilyState.Outdated, "ЖК Полковая", Hash("b"), $@"{Root}\{Polk}"));
        (rows[1].State, rows[1].IterationId, rows[1].CurrentHash).ShouldBe((ProjectFamilyState.Current, (Guid?)null, Hash("a")));

        (await _coordinator.PostJsonAsync($"/api/families/iterations/{iteration}/final", new IterationFinalRequest(null, "сдано"))).EnsureSuccessStatusCode();
        var final = (await (await designer.PostJsonAsync("/api/families/project-status",
            new ProjectStatusRequest([new ProjectFamilyDto("Дверь", card, Hash("a")) { IterationId = iteration }], false))).Content
            .ReadFromJsonAsync<List<ProjectFamilyStatus>>(TestJson.Options))!.Single();
        (final.State, final.IterationFinal).ShouldBe((ProjectFamilyState.Current, true));
    }

    [Test]
    public async Task Library_HasIterationsFolder()
    {
        await StartAsync();

        (await (await ClientAsync(UserRole.Designer)).GetJsonAsync<LibraryInfo>("/api/families/library")).IterationsFolder.ShouldBe("_Итерации");
    }
}
