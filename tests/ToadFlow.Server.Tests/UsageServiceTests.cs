using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using ToadFlow.Contracts;
using ToadFlow.Server.Tests.Infrastructure;
using ToadFlow.Server.Usage.Data;
using ToadFlow.Server.Usage.Service;
using static ToadFlow.Server.Tests.Infrastructure.UsageFixtures;

namespace ToadFlow.Server.Tests;

public class UsageServiceTests : ServerTest
{
    public sealed record Basis(int People, int Machines);

    public sealed record DayPeople(DateOnly Day, int People);

    public sealed record SpaceVisits(string Space, int Visits, int People);

    public sealed record PageVisits(string Space, string Page, int Visits, int People);

    public sealed record RoleVisits(string Role, int People, int Visits);

    public sealed record Web(DayPeople[] Days, SpaceVisits[] Spaces, PageVisits[] Pages, RoleVisits[] Roles);

    public sealed record AreaCalls(string Area, int Calls, int People);

    public sealed record UserArea(string Area, int Calls);

    public sealed record UserCalls(Guid Id, string Name, UserArea[] Areas);

    public sealed record Plugins(AreaCalls[] Areas, UserCalls[] Users);

    public sealed record Service(Basis Basis, Web Web, Plugins Plugins);

    private sealed record TokenBody(string Token);

    private async Task<HttpClient> TokenClientAsync(HttpClient user)
    {
        var created = await (await user.PostAsJsonAsync("/api/auth/tokens", new { name = "ToadTools" }, TestJson.Options)).Content
            .ReadFromJsonAsync<TokenBody>(TestJson.Options);
        var client = Server.CreateClient();
        client.DefaultRequestHeaders.Add("X-ToadFlow-Token", created!.Token);
        return client;
    }

    private Task FlushAsync() => Server.Services.GetRequiredService<PluginCallCounter>().FlushAsync(CancellationToken.None);

    private Task<List<UsageWebDay>> WebDaysAsync() => DbAsync(db => db.Set<UsageWebDay>().AsNoTracking().ToListAsync());

    private Task<List<UsagePluginDay>> PluginDaysAsync() => DbAsync(db => db.Set<UsagePluginDay>().AsNoTracking().ToListAsync());

    [Test]
    public async Task PageView_Increments_PerUserDayPage()
    {
        var viewer = await ClientAsync(UserRole.Viewer);

        (await viewer.PostJsonAsync("/api/usage/pageview", new { space = "clash", page = "/clash/checks" })).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        await viewer.PostJsonAsync("/api/usage/pageview", new { space = "clash", page = "/clash/checks" });
        await viewer.PostJsonAsync("/api/usage/pageview", new { space = "clash", page = "/clash/summary" });

        var rows = await WebDaysAsync();
        rows.Single(r => r.Page == "/clash/checks").Count.ShouldBe(2);
        rows.Single(r => r.Page == "/clash/summary").Count.ShouldBe(1);
        rows.ShouldAllBe(r => r.Day == Today);
    }

    [Test]
    [Arguments("clash", "clash/checks")]
    [Arguments("clash", "/clash/checks?x=1")]
    [Arguments("", "/clash")]
    [Arguments("clash", "")]
    public async Task PageView_BadPage_400(string space, string page)
    {
        var response = await (await ClientAsync(UserRole.Viewer)).PostJsonAsync("/api/usage/pageview", new { space, page });

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Test]
    public async Task PageView_TooLong_400()
    {
        var response = await (await ClientAsync(UserRole.Viewer)).PostJsonAsync("/api/usage/pageview", new { space = "clash", page = "/" + new string('a', 100) });

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Test]
    public async Task PageView_ByToken_NotCounted()
    {
        var token = await TokenClientAsync(await ClientAsync(UserRole.Viewer));

        (await token.PostJsonAsync("/api/usage/pageview", new { space = "clash", page = "/clash/checks" })).StatusCode.ShouldBe(HttpStatusCode.NoContent);

        (await WebDaysAsync()).ShouldBeEmpty();
    }

    [Test]
    public async Task PluginCalls_ByToken_CountedByArea_AfterFlush()
    {
        var token = await TokenClientAsync(await ClientAsync(UserRole.Designer));

        await token.GetAsync("/api/clash/my/summary");
        await token.GetAsync("/api/clash/checks");
        await token.GetAsync("/api/families/library");
        await FlushAsync();
        await token.GetAsync("/api/clash/checks");
        await FlushAsync();

        var rows = await PluginDaysAsync();
        rows.Single(r => r.Area == "clash").Count.ShouldBe(3);
        rows.Single(r => r.Area == "families").Count.ShouldBe(1);
    }

    [Test]
    public async Task PluginCalls_ByJwt_NotCounted()
    {
        var viewer = await ClientAsync(UserRole.Viewer);

        await viewer.GetAsync("/api/clash/checks");
        await FlushAsync();

        (await PluginDaysAsync()).ShouldBeEmpty();
    }

    [Test]
    public async Task PluginCalls_UsageAndCoreAreas_NotCounted()
    {
        var token = await TokenClientAsync(await ClientAsync(UserRole.Designer));

        await token.PostAsJsonAsync("/api/usage/events", Batch(Operation()), TestJson.Options);
        await token.GetAsync("/api/auth/me");
        await token.GetAsync("/api/modules");
        await FlushAsync();

        (await PluginDaysAsync()).ShouldBeEmpty();
    }

    [Test]
    public async Task Service_AggregatesWebAndPlugins()
    {
        var viewer = await ClientAsync(UserRole.Viewer);
        var designer = await ClientAsync(UserRole.Designer);
        await viewer.PostJsonAsync("/api/usage/pageview", new { space = "clash", page = "/clash/checks" });
        await designer.PostJsonAsync("/api/usage/pageview", new { space = "clash", page = "/clash/checks" });
        await designer.PostJsonAsync("/api/usage/pageview", new { space = "families", page = "/families/catalog" });
        var token = await TokenClientAsync(designer);
        await token.GetAsync("/api/clash/checks");
        await FlushAsync();

        var service = await (await ClientAsync(UserRole.Viewer)).GetJsonAsync<Service>("/api/usage/service");

        service.Web.Days.ShouldBe([new DayPeople(Today, 2)]);
        service.Web.Spaces.Single(s => s.Space == "clash").ShouldBe(new SpaceVisits("clash", 2, 2));
        service.Web.Pages.Single(p => p.Page == "/families/catalog").Visits.ShouldBe(1);
        service.Web.Roles.Single(r => r.Role == "Designer").ShouldBe(new RoleVisits("Designer", 1, 2));
        service.Plugins.Areas.ShouldBe([new AreaCalls("clash", 1, 1)]);
        service.Plugins.Users.ShouldHaveSingleItem().Id.ShouldBe(await UserIdAsync(designer));
        service.Basis.People.ShouldBe(2);
    }

    [Test]
    public async Task Service_Designer_403()
    {
        (await (await ClientAsync(UserRole.Designer)).GetAsync("/api/usage/service")).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Test]
    public async Task Designer_PageView_Counted()
    {
        var designer = await ClientAsync(UserRole.Designer);

        (await designer.PostJsonAsync("/api/usage/pageview", new { space = "clash", page = "/clash/my" })).StatusCode.ShouldBe(HttpStatusCode.NoContent);

        (await WebDaysAsync()).ShouldHaveSingleItem().UserId.ShouldBe(await UserIdAsync(designer));
    }
}

public class UsageServiceDisabledTests : ServerTest
{
    public UsageServiceDisabledTests() => Modules = "export,clash";

    [Test]
    public async Task Disabled_NoCounterAndNoPageView()
    {
        Server.Services.GetService<PluginCallCounter>().ShouldBeNull();
        (await (await ClientAsync(UserRole.Viewer)).PostJsonAsync("/api/usage/pageview", new { space = "clash", page = "/clash" })).StatusCode
            .ShouldBe(HttpStatusCode.NotFound);
    }
}
