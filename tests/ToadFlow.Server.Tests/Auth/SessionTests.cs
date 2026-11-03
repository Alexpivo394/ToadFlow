using System.Net;
using System.Net.Http.Headers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using ToadFlow.Contracts;
using ToadFlow.Server.Auth;
using ToadFlow.Server.Data.Entities;
using ToadFlow.Server.Tests.Infrastructure;

namespace ToadFlow.Server.Tests;

/// <summary>«Оставаться в системе» (обновляемые токены), отзыв сессий штампом безопасности, ограничения полей.</summary>
public class SessionTests : ServerTest
{
    private sealed record Session(string Token, string? RefreshToken);

    private async Task<Session> LoginAsync(string login, string password, bool remember)
    {
        var response = await Server.CreateClient().PostJsonAsync("/api/auth/login", new { login, password, remember });
        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        return await response.ReadJsonAsync<Session>();
    }

    private Task<HttpResponseMessage> RefreshAsync(string refreshToken) =>
        Server.CreateClient().PostJsonAsync("/api/auth/refresh", new { refreshToken });

    private async Task<HttpStatusCode> MeAsync(string token)
    {
        var client = Server.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return (await client.GetAsync("/api/auth/me")).StatusCode;
    }

    private async Task<(Guid Id, string Login)> UserAsync(UserRole role)
    {
        var login = $"u{Guid.NewGuid():N}"[..12];
        var id = await DbAsync(async db =>
        {
            var user = User.Create(login, "Пользователь " + login, role, "", DateTimeOffset.UtcNow);
            user.PasswordHash = new Microsoft.AspNetCore.Identity.PasswordHasher<User>().HashPassword(user, UserPassword);
            db.Users.Add(user);
            await db.SaveChangesAsync();
            return user.Id;
        });
        return (id, login);
    }

    [Test]
    public async Task Login_NoRemember_NoRefresh()
    {
        var (_, login) = await UserAsync(UserRole.Viewer);

        var session = await LoginAsync(login, UserPassword, remember: false);

        session.RefreshToken.ShouldBeNull();
        (await MeAsync(session.Token)).ShouldBe(HttpStatusCode.OK);
    }

    [Test]
    public async Task Login_Remember_ReturnsRefresh_RefreshRotates()
    {
        var (_, login) = await UserAsync(UserRole.Viewer);
        var first = await LoginAsync(login, UserPassword, remember: true);
        first.RefreshToken.ShouldNotBeNullOrEmpty();

        var response = await RefreshAsync(first.RefreshToken!);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var second = await response.ReadJsonAsync<Session>();
        second.RefreshToken.ShouldNotBeNullOrEmpty();
        second.RefreshToken.ShouldNotBe(first.RefreshToken);
        (await MeAsync(second.Token)).ShouldBe(HttpStatusCode.OK);
        var stored = await DbAsync(db => db.Set<RefreshToken>().AsNoTracking().OrderBy(t => t.CreatedAt).ToListAsync());
        stored.Count.ShouldBe(2);
        stored[0].RevokedAt.ShouldNotBeNull();
        stored[1].ExpiresAt.ShouldBeGreaterThan(DateTimeOffset.UtcNow.AddDays(29));
        stored.ShouldAllBe(t => t.TokenHash.Length == 64); // в базе только SHA-256
    }

    /// <summary>Отозванный токен предъявили снова — его украли: отзываются все токены пользователя.</summary>
    [Test]
    public async Task Refresh_Reused_RevokesFamily401()
    {
        var (_, login) = await UserAsync(UserRole.Viewer);
        var first = await LoginAsync(login, UserPassword, remember: true);
        var second = await (await RefreshAsync(first.RefreshToken!)).ReadJsonAsync<Session>();

        (await RefreshAsync(first.RefreshToken!)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);

        (await RefreshAsync(second.RefreshToken!)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Test]
    public async Task Logout_RevokesRefreshToken()
    {
        var (_, login) = await UserAsync(UserRole.Viewer);
        var session = await LoginAsync(login, UserPassword, remember: true);

        (await Server.CreateClient().PostJsonAsync("/api/auth/logout", new { refreshToken = session.RefreshToken })).StatusCode
            .ShouldBe(HttpStatusCode.NoContent);

        (await RefreshAsync(session.RefreshToken!)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Test]
    public async Task PasswordChanged_OldJwt401_RefreshRevoked()
    {
        var (id, login) = await UserAsync(UserRole.Designer);
        var session = await LoginAsync(login, UserPassword, remember: true);
        var admin = await AdminAsync();

        (await admin.PostJsonAsync($"/api/users/{id}/password", new { password = "new-password-1" })).EnsureSuccessStatusCode();

        (await MeAsync(session.Token)).ShouldBe(HttpStatusCode.Unauthorized);
        (await RefreshAsync(session.RefreshToken!)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await MeAsync((await LoginAsync(login, "new-password-1", remember: false)).Token)).ShouldBe(HttpStatusCode.OK);
    }

    /// <summary>Свой пароль: старые сессии закрыты, а эта получает новый токен и продолжает работать.</summary>
    [Test]
    public async Task OwnPasswordChanged_CurrentSessionGetsNewToken()
    {
        var (_, login) = await UserAsync(UserRole.Designer);
        var other = await LoginAsync(login, UserPassword, remember: false);
        var client = await LoginAsync(login, UserPassword);

        var response = await client.PostJsonAsync("/api/profile/password", new { current = UserPassword, @new = "new-password-1" });

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await MeAsync((await response.ReadJsonAsync<Session>()).Token)).ShouldBe(HttpStatusCode.OK);
        (await MeAsync(other.Token)).ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Test]
    public async Task RoleDowngraded_OldJwtRejected()
    {
        var (id, login) = await UserAsync(UserRole.Operator);
        var session = await LoginAsync(login, UserPassword, remember: false);
        var admin = await AdminAsync();

        (await admin.PutJsonAsync($"/api/users/{id}", new { displayName = "Понижен", role = UserRole.Viewer, isDisabled = false })).EnsureSuccessStatusCode();

        (await MeAsync(session.Token)).ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Test]
    public async Task Register_LoginTooLongOrBadChars_400()
    {
        var anonymous = Server.CreateClient();
        object Body(string login, string name = "Иван Петров") => new { login, displayName = name, password = "secret-123", disciplines = Array.Empty<string>() };

        (await anonymous.PostJsonAsync("/api/auth/register", Body(new string('a', 65)))).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await anonymous.PostJsonAsync("/api/auth/register", Body("иван петров"))).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await anonymous.PostJsonAsync("/api/auth/register", Body("ivan", new string('я', 201)))).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await anonymous.PostJsonAsync("/api/auth/register", Body("ivan.petrov_2-ov"))).StatusCode.ShouldBe(HttpStatusCode.Accepted);
    }

    [Test]
    public async Task Operator_CannotEditAdminOrOperatorName_403()
    {
        var operatorClient = await ClientAsync(UserRole.Operator);
        var (otherOperator, _) = await UserAsync(UserRole.Operator);
        var (designer, _) = await UserAsync(UserRole.Designer);
        var adminId = await DbAsync(db => db.Users.Where(u => u.Login == ServerFactory.AdminLogin).Select(u => u.Id).SingleAsync());
        object Body(string name) => new { displayName = name, position = (string?)null, email = (string?)null, phone = (string?)null };

        (await operatorClient.PutJsonAsync($"/api/users/{adminId}/contacts", Body("Подмена"))).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await operatorClient.PutJsonAsync($"/api/users/{otherOperator}/contacts", Body("Подмена"))).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await operatorClient.PutJsonAsync($"/api/users/{designer}/contacts", Body("Проектировщик"))).StatusCode.ShouldBe(HttpStatusCode.OK);
    }
}

/// <summary>Частота входа и регистрации с одного адреса.</summary>
public class RateLimitTests : ServerTest
{
    public RateLimitTests() => Services = services => services.PostConfigure<AuthLimits>(limits =>
    {
        limits.LoginPerMinute = 10;
        limits.RegisterPer10Minutes = 5;
    });

    [Test]
    public async Task Login_RateLimited_10PerMinutePerIp_429()
    {
        var anonymous = Server.CreateClient();
        var codes = new List<HttpStatusCode>();
        for (var i = 0; i < 11; i++) codes.Add((await anonymous.PostJsonAsync("/api/auth/login", new { login = "nobody", password = "wrong-pass" })).StatusCode);

        codes.Take(10).ShouldAllBe(c => c == HttpStatusCode.Unauthorized);
        codes[10].ShouldBe(HttpStatusCode.TooManyRequests);
    }

    [Test]
    public async Task Register_RateLimited_5Per10MinutesPerIp_429()
    {
        var anonymous = Server.CreateClient();
        var codes = new List<HttpStatusCode>();
        for (var i = 0; i < 6; i++)
            codes.Add((await anonymous.PostJsonAsync("/api/auth/register",
                new { login = $"user{i}", displayName = $"Имя {i}", password = "secret-123", disciplines = Array.Empty<string>() })).StatusCode);

        codes.Take(5).ShouldAllBe(c => c == HttpStatusCode.Accepted);
        codes[5].ShouldBe(HttpStatusCode.TooManyRequests);
    }
}
