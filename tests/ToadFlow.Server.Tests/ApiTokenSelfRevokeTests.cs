using System.Net;
using Shouldly;
using ToadFlow.Contracts;
using ToadFlow.Server.Tests.Infrastructure;

namespace ToadFlow.Server.Tests;

/// <summary>Выход из ToadTools: токен отзывает сам себя и только себя.</summary>
public class ApiTokenSelfRevokeTests : ServerTest
{
    private async Task<HttpClient> TokenClientAsync(HttpClient user, string name)
    {
        var created = await (await user.PostJsonAsync("/api/auth/tokens", new { name })).EnsureSuccessStatusCode().ReadJsonAsync<TokenCreatedView>();
        var client = Server.CreateClient();
        client.DefaultRequestHeaders.Add("X-ToadFlow-Token", created.Token);
        return client;
    }

    [Test]
    public async Task SelfRevoke_ByToken_RevokesOnlyThatToken()
    {
        var designer = await ClientAsync(UserRole.Designer);
        var first = await TokenClientAsync(designer, "ToadTools — PC1");
        var second = await TokenClientAsync(designer, "ToadTools — PC2");

        (await first.DeleteAsync("/api/auth/tokens/current")).StatusCode.ShouldBe(HttpStatusCode.NoContent);

        (await first.GetAsync("/api/auth/me")).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await second.GetAsync("/api/auth/me")).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await designer.GetJsonAsync<TokenView[]>("/api/auth/tokens")).ShouldHaveSingleItem().Name.ShouldBe("ToadTools — PC2");
    }

    [Test]
    public async Task SelfRevoke_ByJwt_BadRequest()
    {
        var designer = await ClientAsync(UserRole.Designer);
        await TokenClientAsync(designer, "ToadTools — PC1");

        var response = await designer.DeleteAsync("/api/auth/tokens/current");

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync()).ShouldContain("Отозвать можно только токен, которым выполнен вход");
        (await designer.GetJsonAsync<TokenView[]>("/api/auth/tokens")).ShouldHaveSingleItem();
    }

    [Test]
    public async Task SelfRevoke_OtherEndpoints_StillForbiddenByToken()
    {
        var designer = await ClientAsync(UserRole.Designer);
        var token = await TokenClientAsync(designer, "ToadTools — PC1");
        var id = (await designer.GetJsonAsync<TokenView[]>("/api/auth/tokens")).ShouldHaveSingleItem().Id;

        (await token.GetAsync("/api/auth/tokens")).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await token.DeleteAsync($"/api/auth/tokens/{id}")).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }
}
