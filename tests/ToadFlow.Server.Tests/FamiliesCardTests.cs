using System.Net;
using Microsoft.EntityFrameworkCore;
using Shouldly;
using ToadFlow.Contracts;
using ToadFlow.Server.Families.Cards;
using ToadFlow.Server.Families.Data;
using ToadFlow.Server.Families.Search;
using ToadFlow.Server.Runs;
using ToadFlow.Server.Tests.Infrastructure;
using static ToadFlow.Server.Tests.Infrastructure.FamiliesFixtures;

namespace ToadFlow.Server.Tests;

public class FamiliesCardTests : ServerTest
{
    public FamiliesCardTests() => Services = WithoutScanLoop;

    private FamilyLibrary _library = null!;

    [After(Test)]
    public async Task StopAgentAsync()
    {
        if (_library is not null) await _library.DisposeAsync();
    }

    private async Task<Guid> CardIdAsync(string name) => await DbAsync(db => db.Set<FamilyCard>().Where(c => c.Name == name).Select(c => c.Id).SingleAsync());

    [Test]
    public async Task Card_HasFullPathAndVersions()
    {
        _library = await FamilyLibrary.StartAsync(Server, await AdminAsync());
        await _library.ScanAsync(File(@"ОВ\Клапаны\Клапан.rfa", "a"));
        await _library.ScanAsync(File(@"ОВ\Клапаны\Клапан.rfa", "b", year: 2024));
        var id = await CardIdAsync("Клапан");

        var card = await (await ClientAsync(UserRole.Designer)).GetJsonAsync<FamilyCardView>($"/api/families/{id}");

        (card.Name, card.FullPath, card.Section, card.State, card.RevitYear).ShouldBe(("Клапан", @"\\nas\Families\ОВ\Клапаны\Клапан.rfa", "ОВ", CardState.Approved, 2024));
        card.Content.ShouldBeNull();
        card.CurrentReadState.ShouldBe(ReadState.Pending);
        card.Versions.Select(v => (v.Hash, v.Note, v.IsCurrent, v.ReadState)).ShouldBe([
            (Hash("b"), "изменено без проверки", true, ReadState.Pending),
            (Hash("a"), "", false, ReadState.Pending)
        ]);
    }

    [Test]
    public async Task Card_InReview_HiddenFromDesigner_404()
    {
        var id = await DbAsync(db => SeedCardAsync(db, "Кран", @"_На проверке\Кран.rfa", CardState.InReview, null, DateTimeOffset.UtcNow));
        var designer = await ClientAsync(UserRole.Designer);

        (await designer.GetAsync($"/api/families/{id}")).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await designer.GetAsync($"/api/families/{id}?unreviewed=true")).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await (await ClientAsync(UserRole.Viewer)).GetAsync($"/api/families/{id}")).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Test]
    public async Task Card_Archived_HiddenFromDesigner_404()
    {
        var id = await DbAsync(db => SeedCardAsync(db, "Кран", @"ОВ\Кран.rfa", CardState.Archived, null, DateTimeOffset.UtcNow));

        (await (await ClientAsync(UserRole.Designer)).GetAsync($"/api/families/{id}?unreviewed=true")).StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Test]
    public async Task Card_CandidateHiddenFromDesigner()
    {
        _library = await FamilyLibrary.StartAsync(Server, await AdminAsync());
        await _library.ScanAsync(File(@"ОВ\Клапан.rfa", "a"));
        await _library.ScanAsync(File(@"ОВ\Клапан.rfa", "a"), File(@"_На проверке\Клапан.rfa", "b"));
        var id = await CardIdAsync("Клапан");

        var designer = await (await ClientAsync(UserRole.Designer)).GetJsonAsync<FamilyCardView>($"/api/families/{id}");
        var viewer = await (await ClientAsync(UserRole.Viewer)).GetJsonAsync<FamilyCardView>($"/api/families/{id}");

        designer.Versions.Select(v => v.Hash).ShouldBe([Hash("a")]);
        viewer.Versions.Count.ShouldBe(2);
    }

    [Test]
    public async Task Notes_ByOperator_UpdateSearch()
    {
        var id = await DbAsync(db => SeedCardAsync(db, "Клапан", @"ОВ\Клапан.rfa", CardState.Approved, null, DateTimeOffset.UtcNow));
        var operatorClient = await ClientAsync(UserRole.Operator);

        var response = await operatorClient.PutJsonAsync($"/api/families/{id}/notes", new FamilyNotesRequest("Межфланцевый", ["Веза", " ", "веза"], "ВЕЗА"));

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var card = await operatorClient.GetJsonAsync<FamilyCardView>($"/api/families/{id}");
        (card.Description, card.Manufacturer).ShouldBe(("Межфланцевый", "ВЕЗА"));
        card.Tags.ShouldBe(["Веза"]);
        var page = await (await ClientAsync(UserRole.Designer)).GetJsonAsync<Page<FamilyHit>>("/api/families/search?q=межфланц");
        page.Items.ShouldHaveSingleItem().Id.ShouldBe(id);
    }

    [Test]
    public async Task Notes_ByViewer_403()
    {
        var id = await DbAsync(db => SeedCardAsync(db, "Клапан", @"ОВ\Клапан.rfa", CardState.Approved, null, DateTimeOffset.UtcNow));

        var response = await (await ClientAsync(UserRole.Viewer)).PutJsonAsync($"/api/families/{id}/notes", new FamilyNotesRequest("x", [], null));

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }
}
