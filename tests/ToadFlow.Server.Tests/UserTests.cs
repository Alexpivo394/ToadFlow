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

    /// <summary>Координатор видит пользователей и задаёт разделы; роль, отключение, пароль и удаление — только администратор.</summary>
    [Test]
    public async Task Users_OperatorSetsDisciplines_AdminOnlyChangesRole()
    {
        var coordinator = await ClientAsync(UserRole.Operator);
        var designer = await ClientAsync(UserRole.Designer);
        var target = await designer.GetJsonAsync<UserView>("/api/auth/me");

        (await coordinator.GetAsync("/api/users")).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await coordinator.PutJsonAsync($"/api/users/{target.Id}/disciplines", new[] { "ОВ", "ВК" })).EnsureSuccessStatusCode();
        (await coordinator.PutJsonAsync($"/api/users/{target.Id}/disciplines", new[] { "Сантехника" })).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await DbAsync(db => db.Users.SingleAsync(u => u.Id == target.Id))).Disciplines.ShouldBe(["ОВ", "ВК"]);

        (await coordinator.PutJsonAsync($"/api/users/{target.Id}", new { displayName = "П", role = UserRole.Operator, isDisabled = false }))
            .StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await coordinator.PostJsonAsync($"/api/users/{target.Id}/password", new { password = "new-pass-456" })).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await coordinator.DeleteAsync($"/api/users/{target.Id}")).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await coordinator.PostJsonAsync("/api/users", new { login = "x1", displayName = "X", role = UserRole.Viewer, password = "secret-123" }))
            .StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await designer.PutJsonAsync($"/api/users/{target.Id}/disciplines", new[] { "АР" })).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    /// <summary>Имя и контакты пользователя правит координатор из карточки в «Пользователях»; проектировщик чужие — нет.</summary>
    [Test]
    public async Task Users_OperatorEditsContacts()
    {
        var coordinator = await ClientAsync(UserRole.Operator);
        var designer = await ClientAsync(UserRole.Designer);
        var target = await designer.GetJsonAsync<UserView>("/api/auth/me");

        (await coordinator.PutJsonAsync($"/api/users/{target.Id}/contacts",
            new { displayName = "Ольга Ковалёва", position = "ГИП ОВ", email = "olga@example.com", phone = "+7 900" })).EnsureSuccessStatusCode();
        (await coordinator.PutJsonAsync($"/api/users/{target.Id}/contacts", new { displayName = " ", position = (string?)null })).StatusCode
            .ShouldBe(HttpStatusCode.BadRequest);
        (await coordinator.PutJsonAsync($"/api/users/{target.Id}/contacts", new { displayName = "О", email = "без собаки" })).StatusCode
            .ShouldBe(HttpStatusCode.BadRequest);
        (await designer.PutJsonAsync($"/api/users/{target.Id}/contacts", new { displayName = "Я" })).StatusCode.ShouldBe(HttpStatusCode.Forbidden);

        var user = await DbAsync(db => db.Users.SingleAsync(u => u.Id == target.Id));
        (user.DisplayName, user.Position, user.Email, user.Phone).ShouldBe(("Ольга Ковалёва", "ГИП ОВ", "olga@example.com", "+7 900"));
    }

    private sealed record UserView(Guid Id, string Login, string DisplayName, UserRole Role);
}

/// <summary>Массовые действия над пользователями: разделы — координатор, отключение и включение — администратор.</summary>
public class UserBulkTests : ServerTest
{
    private sealed record BulkResult(int Changed, string[] Skipped);

    private Task<Guid> UserAsync(string name, UserRole role = UserRole.Designer, string[]? disciplines = null, bool disabled = false) => DbAsync(async db =>
    {
        var user = Seed.User(db, role);
        user.DisplayName = name;
        user.Disciplines = disciplines ?? [];
        user.IsDisabled = disabled;
        await db.SaveChangesAsync();
        return user.Id;
    });

    private Task<Data.Entities.User> LoadAsync(Guid id) => DbAsync(db => db.Users.AsNoTracking().SingleAsync(u => u.Id == id));

    [Test]
    public async Task Bulk_AddDiscipline_ToSeveral()
    {
        var a = await UserAsync("Анна", disciplines: ["ОВ"]);
        var b = await UserAsync("Борис");
        var c = await UserAsync("Вера", disciplines: ["АР"]);

        var response = await (await ClientAsync(UserRole.Operator)).PostJsonAsync("/api/users/bulk",
            new { ids = new[] { a, b, c }, action = "AddDiscipline", discipline = "ар" });

        var result = await response.EnsureSuccessStatusCode().ReadJsonAsync<BulkResult>();
        (result.Changed, result.Skipped.Length).ShouldBe((2, 0));
        (await LoadAsync(a)).Disciplines.ShouldBe(["ОВ", "АР"]);
        (await LoadAsync(b)).Disciplines.ShouldBe(["АР"]);
        (await LoadAsync(c)).Disciplines.ShouldBe(["АР"]);
    }

    [Test]
    public async Task Bulk_RemoveDiscipline_IgnoresUsersWithoutIt()
    {
        var a = await UserAsync("Анна", disciplines: ["ОВ", "АР"]);
        var b = await UserAsync("Борис", disciplines: ["ОВ"]);

        var result = await (await (await ClientAsync(UserRole.Operator)).PostJsonAsync("/api/users/bulk",
            new { ids = new[] { a, b }, action = "RemoveDiscipline", discipline = "АР" })).EnsureSuccessStatusCode().ReadJsonAsync<BulkResult>();

        result.Changed.ShouldBe(1);
        (await LoadAsync(a)).Disciplines.ShouldBe(["ОВ"]);
        (await LoadAsync(b)).Disciplines.ShouldBe(["ОВ"]);
    }

    [Test]
    public async Task Bulk_Disable_ByOperator_403()
    {
        var a = await UserAsync("Анна");

        var response = await (await ClientAsync(UserRole.Operator)).PostJsonAsync("/api/users/bulk", new { ids = new[] { a }, action = "Disable" });

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await LoadAsync(a)).IsDisabled.ShouldBeFalse();
    }

    [Test]
    public async Task Bulk_Disable_SkipsSelf()
    {
        var admin = await AdminAsync();
        await UserAsync("Второй админ", UserRole.Admin);
        var a = await UserAsync("Анна");
        var self = await DbAsync(db => db.Users.Where(u => u.Login == ServerFactory.AdminLogin).Select(u => u.Id).SingleAsync());
        var selfName = (await LoadAsync(self)).DisplayName;

        var result = await (await admin.PostJsonAsync("/api/users/bulk", new { ids = new[] { a, self }, action = "Disable" }))
            .EnsureSuccessStatusCode().ReadJsonAsync<BulkResult>();

        result.Changed.ShouldBe(1);
        result.Skipped.ShouldBe([$"{selfName}: нельзя отключить себя"]);
        (await LoadAsync(a)).IsDisabled.ShouldBeTrue();
        (await LoadAsync(self)).IsDisabled.ShouldBeFalse();
    }

    [Test]
    public async Task Bulk_Disable_SkipsLastAdmin()
    {
        // Себя отключить нельзя, поэтому вызывающий администратор всегда остаётся; правило «последний» страхует то же с другой стороны.
        var admin = await AdminAsync();
        var self = await DbAsync(db => db.Users.Where(u => u.Login == ServerFactory.AdminLogin).Select(u => u.Id).SingleAsync());
        var a = await UserAsync("Анна");

        var result = await (await admin.PostJsonAsync("/api/users/bulk", new { ids = new[] { self, a }, action = "Disable" }))
            .EnsureSuccessStatusCode().ReadJsonAsync<BulkResult>();

        result.Changed.ShouldBe(1);
        result.Skipped.ShouldHaveSingleItem();
        (await DbAsync(db => db.Users.CountAsync(u => u.Role == UserRole.Admin && !u.IsDisabled))).ShouldBe(1);
    }

    [Test]
    public async Task Bulk_AddDiscipline_WithoutName_400()
    {
        var a = await UserAsync("Анна");

        var response = await (await ClientAsync(UserRole.Operator)).PostJsonAsync("/api/users/bulk", new { ids = new[] { a }, action = "AddDiscipline" });

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }
}
