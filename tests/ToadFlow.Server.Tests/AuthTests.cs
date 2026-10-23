using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Shouldly;
using ToadFlow.Contracts;
using ToadFlow.Server.Tests.Infrastructure;

namespace ToadFlow.Server.Tests;

public class AuthTests : ServerTest
{
    [Test]
    public async Task Login_SeededAdmin_ReturnsToken()
    {
        var response = await Server.CreateClient().PostJsonAsync("/api/auth/login",
            new { login = ServerFactory.AdminLogin, password = ServerFactory.AdminPassword });

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = await response.ReadJsonAsync<LoginResponse>();
        body!.Token.ShouldNotBeNullOrWhiteSpace();
        body.User.Role.ShouldBe(UserRole.Admin);
        (await DbAsync(db => db.Users.SingleAsync(u => u.Login == ServerFactory.AdminLogin))).LastLoginAt.ShouldNotBeNull();
    }

    [Test]
    public async Task Login_WrongPassword_401()
    {
        var response = await Server.CreateClient().PostJsonAsync("/api/auth/login",
            new { login = ServerFactory.AdminLogin, password = "wrong" });

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Test]
    public async Task Me_WithoutToken_401() =>
        (await Server.CreateClient().GetAsync("/api/auth/me")).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);

    [Test]
    public async Task Me_ReturnsCurrentUser()
    {
        var client = await ClientAsync(UserRole.Operator);

        var me = await client.GetJsonAsync<UserView>("/api/auth/me");

        me!.Role.ShouldBe(UserRole.Operator);
    }

    private sealed record LoginResponse(string Token, UserView User);

    private sealed record UserView(Guid Id, string Login, string DisplayName, UserRole Role);
}
