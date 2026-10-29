using System.Net;
using System.Text.Json;
using Alexpivo394.Revit.Clash.Rules;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using Shouldly;
using ToadFlow.Contracts;
using ToadFlow.Server.Clash;
using ToadFlow.Server.Clash.Parts;
using ToadFlow.Server.Tests.Infrastructure;

namespace ToadFlow.Server.Tests;

public class ClashDraftCollaborationTests : ServerTest
{
    private readonly FakeTimeProvider _time = new(DateTimeOffset.UtcNow);

    public ClashDraftCollaborationTests() => Time = _time;

    private static readonly Condition AnyCategory = new(new PropertyRef(PropertyKind.Category), ConditionOperator.IsNotEmpty, null);

    private static SelectionSet Set(string discipline, string name, Guid? id = null) =>
        new(id ?? Guid.NewGuid(), ConfigurationDefaults.DisciplineGroupId(discipline), name, [new ConditionGroup([AnyCategory])]);

    private async Task<(HttpClient Admin, Guid PartId, ClashConfiguration Config, int Version)> PartAsync()
    {
        var admin = await AdminAsync();
        Guid[] ids;
        using (var scope = Server.Services.CreateScope())
        {
            var storage = scope.ServiceProvider.GetRequiredService<ClashStorage>();
            ids = [(await storage.EnsureModelAsync("POL_K1_AR.rvt", "RSN://rs22/POL_K1_AR.rvt", CancellationToken.None)).Id];
        }

        var part = await (await admin.PostJsonAsync("/api/clash/parts", new
        {
            name = "Часть", models = ids.Select(id => new { modelId = id }), start = new { kind = "empty" }
        })).ReadJsonAsync<ClashPartView>();
        var config = new ClashConfiguration([ConfigurationDefaults.DisciplineGroup("АР")], [Set("АР", "Стены"), Set("АР", "Двери")], [], []);
        var saved = await SaveAsync(admin, part.Id, 0, config);
        saved.StatusCode.ShouldBe(HttpStatusCode.OK);
        var view = await saved.ReadJsonAsync<DraftView>();
        return (admin, part.Id, Parse(view), view.Version);
    }

    private static ClashConfiguration Parse(DraftView view) => ConfigurationJson.Deserialize(view.Configuration.GetRawText());

    private static Task<HttpResponseMessage> SaveAsync(HttpClient client, Guid partId, int baseVersion, ClashConfiguration config, string[]? keepMine = null) =>
        client.PutJsonAsync($"/api/clash/parts/{partId}/draft", new
        {
            baseVersion, configuration = JsonDocument.Parse(ConfigurationJson.Serialize(config)).RootElement, keepMine
        });

    private static ClashConfiguration Rename(ClashConfiguration config, int index, string name) =>
        config with { Sets = config.Sets.Select((s, i) => i == index ? s with { Name = name } : s).ToList() };

    [Test]
    public async Task SaveDraft_Concurrent_MergedOrConflict()
    {
        var (admin, partId, config, version) = await PartAsync();
        var other = await ClientAsync(UserRole.Operator);

        var first = await SaveAsync(admin, partId, version, Rename(config, 0, "Стены наружные"));
        var second = await SaveAsync(other, partId, version, Rename(config, 1, "Двери и люки"));
        var third = await SaveAsync(other, partId, version, Rename(config, 0, "Стены внутренние"));

        first.StatusCode.ShouldBe(HttpStatusCode.OK);
        second.StatusCode.ShouldBe(HttpStatusCode.OK);
        var merged = await second.ReadJsonAsync<DraftView>();
        merged.Version.ShouldBe(version + 2);
        Parse(merged).Sets.Select(s => s.Name).ShouldBe(["Стены наружные", "Двери и люки"]);

        third.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        using var body = JsonDocument.Parse(await third.Content.ReadAsStringAsync());
        var conflict = body.RootElement.GetProperty("conflicts").EnumerateArray().ShouldHaveSingleItem();
        conflict.GetProperty("key").GetString().ShouldBe($"set:{config.Sets[0].Id:N}");
        body.RootElement.GetProperty("current").GetProperty("version").GetInt32().ShouldBe(version + 2);
    }

    [Test]
    public async Task SaveDraft_KeepMine_Overrides()
    {
        var (admin, partId, config, version) = await PartAsync();
        (await SaveAsync(admin, partId, version, Rename(config, 0, "Стены А"))).EnsureSuccessStatusCode();

        var response = await SaveAsync(admin, partId, version, Rename(config, 0, "Стены Б"), [$"set:{config.Sets[0].Id:N}"]);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        Parse(await response.ReadJsonAsync<DraftView>()).Sets[0].Name.ShouldBe("Стены Б");
    }

    [Test]
    public async Task SaveDraft_BaseTooOld_AllConflicts()
    {
        var (admin, partId, config, version) = await PartAsync();
        (await SaveAsync(admin, partId, version, Rename(config, 0, "Стены А"))).EnsureSuccessStatusCode();
        await DbAsync(db => db.Set<ClashDraftVersion>().Where(v => v.PartId == partId && v.Version == version).ExecuteDeleteAsync());

        var response = await SaveAsync(admin, partId, version, Rename(config, 1, "Двери Б"));

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        body.RootElement.GetProperty("conflicts").EnumerateArray().Select(c => c.GetProperty("key").GetString())
            .ShouldContain($"set:{config.Sets[1].Id:N}");
    }

    [Test]
    public async Task SaveDraft_IncrementsVersion_OtherWritesToo()
    {
        var (admin, partId, config, version) = await PartAsync();

        (await admin.PostAsync($"/api/clash/parts/{partId}/draft/matrix/fill", null)).EnsureSuccessStatusCode();
        var view = await admin.GetJsonAsync<DraftView>($"/api/clash/parts/{partId}/draft");

        version.ShouldBe(1);
        view.Version.ShouldBe(2);
        // Правка поверх версии до заполнения матрицы не теряет клетки, которые добавило заполнение.
        var saved = await SaveAsync(admin, partId, version, Rename(config, 0, "Стены"));
        Parse(await saved.ReadJsonAsync<DraftView>()).Matrix.ShouldHaveSingleItem();
    }

    [Test]
    public async Task SaveDraft_LegacyBody_Overwrites()
    {
        var (admin, partId, config, _) = await PartAsync();

        var response = await admin.PutAsync($"/api/clash/parts/{partId}/draft",
            new StringContent(ConfigurationJson.Serialize(Rename(config, 0, "Стены старым клиентом")), System.Text.Encoding.UTF8, "application/json"));

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        Parse(await response.ReadJsonAsync<DraftView>()).Sets[0].Name.ShouldBe("Стены старым клиентом");
    }

    [Test]
    public async Task Presence_HeartbeatListsAndExpires()
    {
        var (admin, partId, config, _) = await PartAsync();
        var viewer = await ClientAsync(UserRole.Viewer);

        (await admin.PostJsonAsync($"/api/clash/parts/{partId}/presence", new { tab = "sets", setId = config.Sets[0].Id })).StatusCode
            .ShouldBe(HttpStatusCode.NoContent);
        (await viewer.PostJsonAsync($"/api/clash/parts/{partId}/presence", new { tab = "draft" })).StatusCode.ShouldBe(HttpStatusCode.NoContent);

        var present = await viewer.GetJsonAsync<PresenceView[]>($"/api/clash/parts/{partId}/presence");
        present.Select(p => p.Tab).Order().ShouldBe(["draft", "sets"]);
        present.Single(p => p.Tab == "sets").SetId.ShouldBe(config.Sets[0].Id);
        present.Single(p => p.Tab == "sets").DisplayName.ShouldNotBeNullOrWhiteSpace();

        _time.Advance(TimeSpan.FromSeconds(30));
        (await viewer.PostJsonAsync($"/api/clash/parts/{partId}/presence", new { tab = "draft" })).EnsureSuccessStatusCode();
        _time.Advance(TimeSpan.FromSeconds(15));

        (await viewer.GetJsonAsync<PresenceView[]>($"/api/clash/parts/{partId}/presence")).ShouldHaveSingleItem().Tab.ShouldBe("draft");

        (await viewer.DeleteAsync($"/api/clash/parts/{partId}/presence")).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await viewer.GetJsonAsync<PresenceView[]>($"/api/clash/parts/{partId}/presence")).ShouldBeEmpty();
    }
}
