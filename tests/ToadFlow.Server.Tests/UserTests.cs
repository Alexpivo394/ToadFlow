using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Shouldly;
using ToadFlow.Contracts;
using ToadFlow.Server.Tests.Infrastructure;

namespace ToadFlow.Server.Tests;

public class UserTests : ServerTest
{
    [Test]
    public async Task Users_ViewerCannotCreate_403()
    {
        var viewer = await ClientAsync(UserRole.Viewer);

        var response = await viewer.PostJsonAsync("/api/users",
            new { login = "petrov", displayName = "Петров", role = UserRole.Operator, password = "secret-123" });

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Test]
    public async Task Users_AdminCreates_ThenUserLogsIn()
    {
        var admin = await AdminAsync();

        var response = await admin.PostJsonAsync("/api/users",
            new { login = "Petrov", displayName = "Иван Петров", role = UserRole.Operator, password = "secret-123" });

        response.StatusCode.ShouldBe(HttpStatusCode.Created);
        var petrov = await LoginAsync("petrov", "secret-123");
        (await petrov.GetAsync("/api/auth/me")).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Test]
    public async Task Users_DuplicateLogin_409()
    {
        var admin = await AdminAsync();
        await admin.PostJsonAsync("/api/users", new { login = "petrov", displayName = "П", role = UserRole.Viewer, password = "secret-123" });

        var response = await admin.PostJsonAsync("/api/users",
            new { login = "PETROV", displayName = "П2", role = UserRole.Viewer, password = "secret-123" });

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
    }

    [Test]
    public async Task Users_DisabledUser_TokenRejected()
    {
        var admin = await AdminAsync();
        var operatorClient = await ClientAsync(UserRole.Operator);
        var me = await operatorClient.GetJsonAsync<UserView>("/api/auth/me");

        (await admin.PutJsonAsync($"/api/users/{me!.Id}", new { displayName = me.DisplayName, role = me.Role, isDisabled = true }))
            .EnsureSuccessStatusCode();

        (await operatorClient.GetAsync("/api/auth/me")).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Test]
    public async Task Users_CannotDemoteLastAdmin_409()
    {
        var admin = await AdminAsync();
        var adminId = await DbAsync(db => db.Users.Where(u => u.Login == ServerFactory.AdminLogin).Select(u => u.Id).SingleAsync());

        var response = await admin.PutJsonAsync($"/api/users/{adminId}",
            new { displayName = "Админ", role = UserRole.Operator, isDisabled = false });

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await response.Content.ReadAsStringAsync()).ShouldContain("администратор");
    }

    [Test]
    public async Task Users_ResetPassword()
    {
        var admin = await AdminAsync();
        var operatorClient = await ClientAsync(UserRole.Operator);
        var me = await operatorClient.GetJsonAsync<UserView>("/api/auth/me");

        (await admin.PostJsonAsync($"/api/users/{me!.Id}/password", new { password = "new-pass-456" })).EnsureSuccessStatusCode();

        (await LoginAsync(me.Login, "new-pass-456")).ShouldNotBeNull();
    }

    [Test]
    public async Task Users_Create_WritesSystemEventWithAuthor()
    {
        var admin = await AdminAsync();

        await admin.PostJsonAsync("/api/users", new { login = "petrov", displayName = "Иван Петров", role = UserRole.Operator, password = "secret-123" });

        await EventuallyAsync(async () =>
            (await DbAsync(db => db.SystemEvents.ToListAsync())).Any(e => e.Text.Contains("Иван Петров") && e.ActorName == "Администратор"));
    }

    private sealed record UserView(Guid Id, string Login, string DisplayName, UserRole Role);
}
