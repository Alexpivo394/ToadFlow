using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;
using ToadFlow.Contracts;
using ToadFlow.Server.Auth;
using ToadFlow.Server.Data.Entities;
using ToadFlow.Server.Maintenance;
using ToadFlow.Server.Naming;
using ToadFlow.Server.Users;
using ToadFlow.Server.Tests.Infrastructure;
using static ToadFlow.Server.Tests.Infrastructure.ClashDraftFixtures;

namespace ToadFlow.Server.Tests;

/// <summary>Самостоятельная регистрация проектировщика: заявка ждёт координатора, до подтверждения войти нельзя.</summary>
public class RegistrationTests : ServerTest
{
    private static object Request(string login, params string[] disciplines) => new
    {
        login, displayName = "Ольга Ковалёва", password = "secret-123", disciplines,
        position = "Инженер ОВ", email = "olga@example.com", phone = "+7 900 000-00-00"
    };

    private static Task<HttpResponseMessage> RegisterAsync(HttpClient client, string login, params string[] disciplines) =>
        client.PostJsonAsync("/api/auth/register", Request(login, disciplines));

    [Test]
    public async Task Register_CreatesPendingDesigner()
    {
        var response = await RegisterAsync(Server.CreateClient(), "Kovaleva", "ОВ", "ВК");

        response.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        var user = await DbAsync(db => db.Users.SingleAsync(u => u.Login == "kovaleva"));
        (user.Role, user.IsPending, user.Position, user.Email, user.Phone).ShouldBe((UserRole.Designer, true, "Инженер ОВ", "olga@example.com", "+7 900 000-00-00"));
        user.Disciplines.ShouldBe(["ОВ", "ВК"]);
        await EventuallyAsync(async () => (await DbAsync(db => db.SystemEvents.ToListAsync())).Any(e => e.Text.Contains("Заявка на регистрацию") && e.Text.Contains("Ольга Ковалёва")));
    }

    [Test]
    public async Task Register_LoginTaken_409()
    {
        (await RegisterAsync(Server.CreateClient(), "kovaleva", "ОВ")).EnsureSuccessStatusCode();

        (await RegisterAsync(Server.CreateClient(), "KOVALEVA", "ОВ")).StatusCode.ShouldBe(HttpStatusCode.Conflict);
    }

    [Test]
    public async Task Register_UnknownDiscipline_400()
    {
        var response = await RegisterAsync(Server.CreateClient(), "kovaleva", "Сантехника");

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync()).ShouldContain("Сантехника");
    }

    /// <summary>Без капчи заявки ограничены числом ждущих: 51-я получает 429.</summary>
    [Test]
    public async Task Register_PendingCap_429()
    {
        await DbAsync(async db =>
        {
            for (var i = 0; i < 50; i++)
            {
                var user = User.Create($"pending-{i}", "Ждёт", UserRole.Designer, "", DateTimeOffset.UtcNow);
                user.IsPending = true;
                db.Users.Add(user);
            }

            await db.SaveChangesAsync();
        });

        (await RegisterAsync(Server.CreateClient(), "kovaleva", "ОВ")).StatusCode.ShouldBe(HttpStatusCode.TooManyRequests);
    }

    [Test]
    public async Task Login_Pending_403WithReason()
    {
        (await RegisterAsync(Server.CreateClient(), "kovaleva", "ОВ")).EnsureSuccessStatusCode();

        var response = await Server.CreateClient().PostJsonAsync("/api/auth/login", new { login = "kovaleva", password = "secret-123" });

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await response.ReadJsonAsync<ErrorBody>()).Error.ShouldBe("Заявка ждёт подтверждения координатора");
        (await Server.CreateClient().PostJsonAsync("/api/auth/login", new { login = "kovaleva", password = "wrong-pass" }))
            .StatusCode.ShouldBe(HttpStatusCode.Unauthorized); // неверный пароль заявку не выдаёт
    }

    [Test]
    public async Task Approve_ByOperator_CanLogin_WithDisciplines()
    {
        (await RegisterAsync(Server.CreateClient(), "kovaleva", "ОВ", "ВК")).EnsureSuccessStatusCode();
        var coordinator = await ClientAsync(UserRole.Operator);
        var pending = (await coordinator.GetJsonAsync<UserView[]>("/api/users/pending")).ShouldHaveSingleItem();
        pending.Disciplines.ShouldBe(["ОВ", "ВК"]);

        (await coordinator.PostJsonAsync($"/api/users/{pending.Id}/approve", new { disciplines = new[] { "ОВ" } })).EnsureSuccessStatusCode();

        var olga = await LoginAsync("kovaleva", "secret-123");
        var me = await olga.GetJsonAsync<UserView>("/api/auth/me");
        (me.Role, me.IsPending).ShouldBe((UserRole.Designer, false));
        me.Disciplines.ShouldBe(["ОВ"]);
        (await coordinator.GetJsonAsync<UserView[]>("/api/users/pending")).ShouldBeEmpty();
        await EventuallyAsync(async () => (await DbAsync(db => db.SystemEvents.ToListAsync())).Any(e => e.Text.Contains("Подтверждена заявка")));
    }

    [Test]
    public async Task Reject_DeletesUser()
    {
        (await RegisterAsync(Server.CreateClient(), "kovaleva", "ОВ")).EnsureSuccessStatusCode();
        var coordinator = await ClientAsync(UserRole.Operator);
        var id = (await coordinator.GetJsonAsync<UserView[]>("/api/users/pending")).Single().Id;

        (await coordinator.PostAsync($"/api/users/{id}/reject", null)).EnsureSuccessStatusCode();

        (await DbAsync(db => db.Users.AnyAsync(u => u.Id == id))).ShouldBeFalse();
        var me = await coordinator.GetJsonAsync<UserView>("/api/auth/me");
        (await coordinator.PostAsync($"/api/users/{me.Id}/reject", null)).StatusCode.ShouldBe(HttpStatusCode.BadRequest); // не заявка
        await EventuallyAsync(async () => (await DbAsync(db => db.SystemEvents.ToListAsync())).Any(e => e.Text.Contains("Отклонена заявка")));
    }

    /// <summary>Заявка живёт 3 суток: потом её нет ни в списке, ни для входа, логин свободен.</summary>
    [Test]
    public async Task Pending_After3Days_Deleted()
    {
        (await RegisterAsync(Server.CreateClient(), "kovaleva", "ОВ")).EnsureSuccessStatusCode();
        (await RegisterAsync(Server.CreateClient(), "petrov", "ВК")).EnsureSuccessStatusCode();
        await ExpireAsync("kovaleva");
        var coordinator = await ClientAsync(UserRole.Operator);

        (await coordinator.GetJsonAsync<UserView[]>("/api/users/pending")).Select(u => u.Login).ShouldBe(["petrov"]);
        (await DbAsync(db => db.Users.AnyAsync(u => u.Login == "kovaleva"))).ShouldBeFalse();
        (await RegisterAsync(Server.CreateClient(), "kovaleva", "ОВ")).StatusCode.ShouldBe(HttpStatusCode.Accepted);
        await EventuallyAsync(async () =>
            (await DbAsync(db => db.SystemEvents.ToListAsync())).Any(e => e.Text.Contains("Заявка удалена") && e.Text.Contains("kovaleva")));
    }

    /// <summary>Просроченная заявка не входит, даже если её ещё не удалили.</summary>
    [Test]
    public async Task Login_ExpiredPending_401()
    {
        (await RegisterAsync(Server.CreateClient(), "kovaleva", "ОВ")).EnsureSuccessStatusCode();
        await ExpireAsync("kovaleva");

        (await Server.CreateClient().PostJsonAsync("/api/auth/login", new { login = "kovaleva", password = "secret-123" })).StatusCode
            .ShouldBe(HttpStatusCode.Unauthorized);
        (await DbAsync(db => db.Users.AnyAsync(u => u.Login == "kovaleva"))).ShouldBeFalse();
    }

    /// <summary>Просроченную заявку удаляет и ежедневная уборка, даже если никто не заходил.</summary>
    [Test]
    public async Task Pending_Expired_RemovedByCleaner()
    {
        (await RegisterAsync(Server.CreateClient(), "kovaleva", "ОВ")).EnsureSuccessStatusCode();
        await ExpireAsync("kovaleva");

        await Server.Services.GetServices<IHostedService>().OfType<RetentionCleaner>().Single().CleanAsync(CancellationToken.None);

        (await DbAsync(db => db.Users.AnyAsync(u => u.Login == "kovaleva"))).ShouldBeFalse();
    }

    private Task ExpireAsync(string login) => DbAsync(db => db.Users.Where(u => u.Login == login)
        .ExecuteUpdateAsync(u => u.SetProperty(x => x.CreatedAt, DateTimeOffset.UtcNow - RegistrationEndpoints.PendingLifetime - TimeSpan.FromMinutes(1))));

    /// <summary>Разделы — из разбора имён (значение, а без него — куски) и ручные разделы моделей частей коллизий.</summary>
    [Test]
    public async Task Disciplines_FromNamingAndPartOverrides()
    {
        await DbAsync(async db =>
        {
            var store = new NamingStore(db);
            var naming = await store.GetAsync();
            await store.SaveAsync(naming with { Entries = [..naming.Entries, new NamingEntry(NamingFields.Discipline, "ТХ")] });
        });
        var admin = await AdminAsync();
        await using var agent = await FakeAgent.RegisterAsync(Server, admin);
        await agent.ConnectAsync("1.8.0", new InstalledRevitDto(2022, true));
        var partId = await PartAsync(Server, admin, agent, "POL_K1_AR.rvt", "POL_K1_OV.rvt");
        var models = await DbAsync(db => db.Set<ToadFlow.Server.Clash.Parts.ClashPartModel>().Where(m => m.PartId == partId).Select(m => m.ModelId).ToListAsync());
        (await admin.PutJsonAsync($"/api/clash/parts/{partId}/models",
            models.Select((id, i) => new { modelId = id, disciplineOverride = i == 0 ? "СС особый" : null }).ToArray())).EnsureSuccessStatusCode();

        var disciplines = await Server.CreateClient().GetJsonAsync<string[]>("/api/auth/disciplines");

        disciplines.ShouldContain("АР");
        disciplines.ShouldContain("ТХ");
        disciplines.ShouldContain("СС особый");
        disciplines.ShouldBeUnique();
        disciplines.ShouldBe(disciplines.Order(StringComparer.Create(System.Globalization.CultureInfo.GetCultureInfo("ru-RU"), false)).ToArray());
    }

    private sealed record ErrorBody(string Error);
}
