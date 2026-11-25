using System.Net;
using Microsoft.EntityFrameworkCore;
using Shouldly;
using ToadFlow.Contracts;
using ToadFlow.Server.Families;
using ToadFlow.Server.Families.Data;
using ToadFlow.Server.Modules;
using ToadFlow.Server.Tests.Infrastructure;

namespace ToadFlow.Server.Tests;

public class FamiliesSettingsTests : ServerTest
{
    [Test]
    public async Task Settings_Defaults()
    {
        var settings = await (await ClientAsync(UserRole.Viewer)).GetJsonAsync<FamiliesSettings>("/api/families/settings");

        settings.ShouldBe(new FamiliesSettings("", "_На проверке", 10, null));
    }

    [Test]
    public async Task Settings_SavedByAdmin_AndLogged()
    {
        var admin = await AdminAsync();

        var response = await admin.PutJsonAsync("/api/families/settings", new FamiliesSettings(@"\\nas\Families", "_Проверка", 15, null));

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await admin.GetJsonAsync<FamiliesSettings>("/api/families/settings")).ShouldBe(new FamiliesSettings(@"\\nas\Families", "_Проверка", 15, null));
        await EventuallyAsync(() => DbAsync(db => db.SystemEvents.AnyAsync(e => e.Text.Contains(@"\\nas\Families"))));
    }

    [Test]
    [Arguments("Families")]
    [Arguments(@"nas\Families")]
    [Arguments("/mnt/families")]
    public async Task Settings_InvalidRoot_400(string root)
    {
        var response = await (await AdminAsync()).PutJsonAsync("/api/families/settings", new FamiliesSettings(root, "_На проверке", 10, null));

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync()).ShouldContain("Корень библиотеки");
    }

    [Test]
    [Arguments(@"Y:\12-BIM\Семейства")]
    [Arguments(@"\\nas\Families\")]
    [Arguments("")]
    public async Task Settings_ValidRoot_Saved(string root)
    {
        var response = await (await AdminAsync()).PutJsonAsync("/api/families/settings", new FamiliesSettings(root, "_На проверке", 10, null));

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Test]
    [Arguments(@"Проверка\Новые")]
    [Arguments("Проверка/Новые")]
    [Arguments("")]
    [Arguments(".toadflow")]
    public async Task Settings_ReviewFolderWithSlash_400(string folder)
    {
        var response = await (await AdminAsync()).PutJsonAsync("/api/families/settings", new FamiliesSettings(@"\\nas\Families", folder, 10, null));

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync()).ShouldContain("Папка проверки");
    }

    [Test]
    [Arguments(@"Итерации\Проекты")]
    [Arguments("")]
    [Arguments(".toadflow")]
    [Arguments("_На проверке")]
    public async Task Settings_InvalidIterationsFolder_400(string folder)
    {
        var response = await (await AdminAsync()).PutJsonAsync("/api/families/settings",
            new FamiliesSettings(@"\\nas\Families", "_На проверке", 10, null, 5, folder));

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync()).ShouldContain("Папка итераций");
    }

    [Test]
    public async Task Settings_IterationsFolder_DefaultAndSaved()
    {
        var admin = await AdminAsync();
        // Параметры, сохранённые до итераций, — без поля: берётся имя по умолчанию.
        await DbAsync(async db =>
        {
            db.Settings.Add(new ToadFlow.Server.Data.Entities.Setting { Key = "families.settings", Value = """{"root":"\\\\nas\\Families","reviewFolder":"_На проверке","scanIntervalMinutes":10}""" });
            await db.SaveChangesAsync();
        });
        (await admin.GetJsonAsync<FamiliesSettings>("/api/families/settings")).IterationsFolder.ShouldBe("_Итерации");

        var response = await admin.PutJsonAsync("/api/families/settings", new FamiliesSettings(@"\\nas\Families", "_На проверке", 10, null, 5, "_Проекты"));

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await admin.GetJsonAsync<FamiliesSettings>("/api/families/settings")).IterationsFolder.ShouldBe("_Проекты");
    }

    [Test]
    [Arguments(0)]
    [Arguments(1441)]
    public async Task Settings_IntervalOutOfRange_400(int minutes)
    {
        var response = await (await AdminAsync()).PutJsonAsync("/api/families/settings", new FamiliesSettings(@"\\nas\Families", "_На проверке", minutes, null));

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Test]
    public async Task Settings_UnknownAgent_400()
    {
        var response = await (await AdminAsync()).PutJsonAsync("/api/families/settings", new FamiliesSettings(@"\\nas\Families", "_На проверке", 10, Guid.NewGuid()));

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Test]
    public async Task Settings_PutByOperator_403()
    {
        var response = await (await ClientAsync(UserRole.Operator)).PutJsonAsync("/api/families/settings", new FamiliesSettings(@"\\nas\Families", "_На проверке", 10, null));

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Test]
    public async Task Module_ListedInModules()
    {
        var modules = await (await ClientAsync(UserRole.Designer)).GetJsonAsync<ModuleView[]>("/api/modules");

        modules.ShouldContain(new ModuleView("families", "Семейства", true));
    }

    [Test]
    public async Task Schedule_OfFamiliesKind_400()
    {
        var admin = await AdminAsync();
        var sourceId = await ScheduleApi.SourceAsync(admin);
        var body = ScheduleApi.Body(sourceId, await ScheduleApi.DefaultPresetAsync(admin));
        var json = System.Text.Json.JsonSerializer.SerializeToNode(body, TestJson.Options)!;
        json["moduleKind"] = ModuleKinds.FamiliesRead;

        var response = await admin.PostJsonAsync("/api/schedules", json);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync()).ShouldContain("У модуля «Семейства» нет расписаний");
    }

    [Test]
    public async Task Card_NameUniqueAmongLiveCards_IgnoringCase()
    {
        var now = DateTimeOffset.UtcNow;
        await DbAsync(async db =>
        {
            db.Set<FamilyCard>().Add(FamiliesFixtures.Card("Клапан", @"ОВ\Клапан.rfa", CardState.Archived, now));
            db.Set<FamilyCard>().Add(FamiliesFixtures.Card("Клапан", @"ВК\Клапан.rfa", CardState.Approved, now));
            await db.SaveChangesAsync();
        });

        await Should.ThrowAsync<DbUpdateException>(() => DbAsync(async db =>
        {
            db.Set<FamilyCard>().Add(FamiliesFixtures.Card("КЛАПАН", @"КР\КЛАПАН.rfa", CardState.InReview, now));
            await db.SaveChangesAsync();
        }));
    }
}

public class FamiliesDisabledTests : ServerTest
{
    public FamiliesDisabledTests() => Modules = "export";

    [Test]
    public async Task Module_Disabled_NoRoutes()
    {
        var admin = await AdminAsync();

        (await admin.GetAsync("/api/families/settings")).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await admin.GetJsonAsync<ModuleView[]>("/api/modules")).ShouldContain(new ModuleView("families", "Семейства", false));
    }
}
