using System.Net;
using System.Text.Json;
using Alexpivo394.Revit.ParamCheck.Summaries;
using Shouldly;
using ToadFlow.Contracts;
using ToadFlow.Server.Data.Entities;
using ToadFlow.Server.Naming;
using ToadFlow.Server.Tests.Infrastructure;

namespace ToadFlow.Server.Tests;

/// <summary>Справочник именования — в ядре: работает без модуля LOI.</summary>
public class NamingWithoutLoiTests : ServerTest
{
    public NamingWithoutLoiTests() => Modules = "export,clash";

    private static readonly NamingConfig Naming = NamingDefaults.Create() with
    {
        Entries = [..NamingDefaults.Create().Entries.Where(e => e.Field != NamingFields.Discipline), new(NamingFields.Discipline, "XX", "XX")]
    };

    [Test]
    public async Task Naming_ReadAndSave_WorkWithoutLoi()
    {
        var admin = await AdminAsync();

        (await admin.GetAsync("/api/naming")).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await admin.PutJsonAsync("/api/naming", Naming)).StatusCode.ShouldBe(HttpStatusCode.OK);

        var saved = await admin.GetJsonAsync<NamingConfig>("/api/naming");
        saved.Entries.ShouldContain(e => e.Field == NamingFields.Discipline && e.Value == "XX");
    }

    [Test]
    public async Task Disciplines_FollowNaming()
    {
        var admin = await AdminAsync();
        (await admin.PutJsonAsync("/api/naming", Naming)).EnsureSuccessStatusCode();

        var disciplines = await Server.CreateClient().GetJsonAsync<string[]>("/api/auth/disciplines");

        disciplines.ShouldContain("XX");
    }
}

public class NamingTests : ServerTest
{
    [Test]
    public async Task OldLoiRoutes_Gone()
    {
        var admin = await AdminAsync();

        (await admin.GetAsync("/api/loi/naming")).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await admin.GetAsync("/api/loi/naming/models")).StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Test]
    public async Task StoredNaming_SurvivesMove()
    {
        var stored = NamingDefaults.Create() with { Entries = [new(NamingFields.Object, "OLD", "Сохранённый прежде")] };
        await DbAsync(async db =>
        {
            db.Settings.Add(new Setting { Key = "loi.naming", Value = JsonSerializer.Serialize(stored, Data.ToadFlowDb.Json) });
            await db.SaveChangesAsync();
        });

        var naming = await (await ClientAsync(UserRole.Viewer)).GetJsonAsync<NamingConfig>("/api/naming");

        naming.Entries.ShouldHaveSingleItem().Tokens.ShouldBe("OLD");
    }

    [Test]
    public async Task Models_IncludeLoiChecksAndSchedules()
    {
        var admin = await AdminAsync();
        await DbAsync(async db =>
        {
            var schedule = Seed.Schedule(db, Seed.Source(db).Id);
            schedule.Selection = new SelectionDto([new ModelSelectionDto("ИЗ_РАСПИСАНИЯ.rvt", "RSN://rs22/ИЗ_РАСПИСАНИЯ.rvt", true, true)], []);
            await db.SaveChangesAsync();
        });
        var check = new CheckSummary
        {
            ModelName = "ИЗ_ПРОВЕРКИ", ModelPath = "RSN://rs22/ИЗ_ПРОВЕРКИ.rvt", ProfileName = "АР", ProfileFingerprint = "abc",
            CheckedAt = DateTimeOffset.UtcNow, Elements = 1, CompleteElements = 1, Values = 1, Filled = 1
        };
        (await admin.PostJsonAsync("/api/loi/checks", new { id = Guid.NewGuid(), summary = check })).EnsureSuccessStatusCode();

        var models = await admin.GetJsonAsync<JsonElement[]>("/api/naming/models");

        models.Select(m => m.GetProperty("name").GetString()).ShouldBe(["ИЗ_ПРОВЕРКИ", "ИЗ_РАСПИСАНИЯ"], ignoreOrder: true);
    }

    [Test]
    public async Task Put_ByOperator_403()
    {
        var response = await (await ClientAsync(UserRole.Operator)).PutJsonAsync("/api/naming", NamingDefaults.Create());

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Test]
    public async Task Preview_ByViewer_Ok()
    {
        var response = await (await ClientAsync(UserRole.Viewer)).PostJsonAsync("/api/naming/preview", new { names = new[] { "POL_AR" } });

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
    }
}
