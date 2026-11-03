using System.Net;
using Microsoft.EntityFrameworkCore;
using Shouldly;
using ToadFlow.Contracts;
using ToadFlow.Server.Auth;
using ToadFlow.Server.Tests.Infrastructure;
using ToadFlow.Server.Users;

namespace ToadFlow.Server.Tests;

/// <summary>Личный кабинет: свои имя и контакты, смена пароля. Роль и разделы сам человек не меняет.</summary>
public class ProfileTests : ServerTest
{
    [Test]
    public async Task Profile_UpdateOwn_RoleAndDisciplinesUnchanged()
    {
        var designer = await ClientAsync(UserRole.Designer);
        var me = await designer.GetJsonAsync<UserView>("/api/profile");
        await DbAsync(db => db.Users.Where(u => u.Id == me.Id).ExecuteUpdateAsync(u => u.SetProperty(x => x.Disciplines, new[] { "ОВ" })));

        var response = await designer.PutJsonAsync("/api/profile", new
        {
            displayName = " Ольга ", position = "ГИП", email = "olga@example.com", phone = "+7 900",
            role = "Admin", disciplines = new[] { "АР" }
        });

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var after = await designer.GetJsonAsync<UserView>("/api/profile");
        (after.DisplayName, after.Position, after.Email, after.Phone, after.Role).ShouldBe(("Ольга", "ГИП", "olga@example.com", "+7 900", UserRole.Designer));
        after.Disciplines.ShouldBe(["ОВ"]);
        (await designer.PutJsonAsync("/api/profile", new { displayName = " " })).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Test]
    public async Task Profile_ChangePassword_WrongCurrent_400()
    {
        var viewer = await ClientAsync(UserRole.Viewer);
        var me = await viewer.GetJsonAsync<UserView>("/api/profile");

        (await viewer.PostJsonAsync("/api/profile/password", new { current = "wrong-pass", @new = "new-pass-456" })).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await viewer.PostJsonAsync("/api/profile/password", new { current = UserPassword, @new = "123" })).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await viewer.PostJsonAsync("/api/profile/password", new { current = UserPassword, @new = "new-pass-456" })).StatusCode.ShouldBe(HttpStatusCode.OK); // с новым токеном этой сессии

        (await LoginAsync(me.Login, "new-pass-456")).ShouldNotBeNull();
    }

    /// <summary>По имени в ленте коллизии коллега видит, как связаться: должность, почта, телефон, разделы.</summary>
    [Test]
    public async Task UserCard_DesignerSeesColleagueContacts()
    {
        var (designer, colleague) = (await ClientAsync(UserRole.Designer), await ClientAsync(UserRole.Designer));
        var other = await colleague.GetJsonAsync<UserView>("/api/profile");
        (await colleague.PutJsonAsync("/api/profile", new { displayName = "Пётр", position = "Инженер КР", email = "petr@example.com", phone = "+7 901" }))
            .EnsureSuccessStatusCode();

        var card = await designer.GetJsonAsync<UserCard>($"/api/users/{other.Id}/card");

        (card.Id, card.DisplayName, card.Position, card.Email, card.Phone).ShouldBe((other.Id, "Пётр", "Инженер КР", "petr@example.com", "+7 901"));
        (await designer.GetAsync($"/api/users/{Guid.NewGuid()}/card")).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await designer.GetAsync("/api/users")).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }
}
