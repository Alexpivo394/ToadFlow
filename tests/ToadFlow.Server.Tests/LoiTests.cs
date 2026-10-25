using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Alexpivo394.Revit.ParamCheck.Profiles;
using Microsoft.EntityFrameworkCore;
using Shouldly;
using ToadFlow.Contracts;
using ToadFlow.Contracts.Loi;
using ToadFlow.Server.Data.Entities;
using ToadFlow.Server.Tests.Infrastructure;

namespace ToadFlow.Server.Tests;

public sealed record LoiSummaryView(Guid Id, string Name, int RuleCount, string ViewName, string WorksetKeyword, string UpdatedBy, string[] Problems);

public sealed record LoiDetailsView(Guid Id, string Name, JsonElement Profile, string[] Problems);

public sealed record LoiImportView(LoiSummaryView[] Created);

public sealed record LoiCatalogSummaryView(int RevitYear, int Categories, int Parameters, bool HasSharedParameterFile);

public sealed record LoiCatalogOverviewView(LoiCatalogSummaryView[] Catalogs, int[] AgentYears);

public sealed record LoiCatalogDetailsView(int RevitYear, LoiCatalogResult Catalog);

public class LoiTests : ServerTest
{
    /// <summary>Профиль так, как его отдаёт редактор в браузере, — в формате файлов ParamChecker.</summary>
    private static JsonNode Profile(string name, bool withRules = true) => JsonNode.Parse(ProfileSerializer.SerializeProfile(new CheckProfile
    {
        Name = name,
        ViewName = "Navisworks",
        WorksetKeyword = "#Связи",
        Models = [new ModelSource { Path = "RSN://rs22/ЖК/АР.rvt" }],
        Rules = withRules
            ? [new CheckRule { Title = "Стены", Filter = new RuleFilter { Categories = ["OST_Walls"] }, Parameters = ["ADSK_Марка"] }]
            : []
    }))!;

    private static async Task<LoiSummaryView> CreateAsync(HttpClient client, string name, bool withRules = true)
    {
        var response = await client.PostJsonAsync("/api/loi/profiles", new { profile = Profile(name, withRules) });
        response.StatusCode.ShouldBe(HttpStatusCode.Created);
        return await response.ReadJsonAsync<LoiSummaryView>();
    }

    private static object ScheduleBody(Guid sourceId, object moduleSettings) => new
    {
        name = "LOI ЖК Север",
        moduleKind = ModuleKinds.Loi,
        sourceId,
        outputFolderTemplate = @"\\nas\loi\{расписание}\{дата}",
        selection = new SelectionDto([new ModelSelectionDto("АР.rvt", "RSN://rs22/ЖК/АР.rvt", true, false)], []),
        moduleSettings,
        recurrenceKind = RecurrenceKind.Weekly,
        daysOfWeekMask = 0b0011111,
        timeOfDay = "20:00:00",
        intervalHours = (int?)null,
        onceAt = (DateTimeOffset?)null,
        priority = 0,
        modelTimeoutMinutes = 30,
        maxRetries = 1,
        isEnabled = true
    };

    [Test]
    public async Task Profiles_OperatorCreates_ModelsDropped_ProblemsReported()
    {
        var operatorClient = await ClientAsync(UserRole.Operator);

        var valid = await CreateAsync(operatorClient, "ОВ");
        var draft = await CreateAsync(operatorClient, "Черновик", withRules: false);

        valid.Problems.ShouldBeEmpty();
        valid.RuleCount.ShouldBe(1);
        valid.ViewName.ShouldBe("Navisworks");
        draft.Problems.ShouldHaveSingleItem().ShouldContain("нет правил");

        var details = await operatorClient.GetJsonAsync<LoiDetailsView>($"/api/loi/profiles/{valid.Id}");
        var profile = ProfileSerializer.DeserializeProfile(details.Profile.GetRawText());
        profile.Models.ShouldBeEmpty();
        profile.Rules.ShouldHaveSingleItem().Parameters.ShouldBe(["ADSK_Марка"]);
    }

    [Test]
    public async Task Profiles_DuplicateName_409_ViewerCannotWrite_403()
    {
        var operatorClient = await ClientAsync(UserRole.Operator);
        await CreateAsync(operatorClient, "ОВ");

        (await operatorClient.PostJsonAsync("/api/loi/profiles", new { profile = Profile("ОВ") })).StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await (await ClientAsync(UserRole.Viewer)).PostJsonAsync("/api/loi/profiles", new { profile = Profile("ВК") }))
            .StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Test]
    public async Task Profiles_Update_Renames()
    {
        var operatorClient = await ClientAsync(UserRole.Operator);
        var created = await CreateAsync(operatorClient, "ОВ");

        var response = await operatorClient.PutJsonAsync($"/api/loi/profiles/{created.Id}", new { profile = Profile("ОВ и ВК") });

        response.EnsureSuccessStatusCode();
        (await operatorClient.GetJsonAsync<LoiSummaryView[]>("/api/loi/profiles")).ShouldHaveSingleItem().Name.ShouldBe("ОВ и ВК");
    }

    [Test]
    public async Task Import_PluginFile_AddsProfiles_RenamesDuplicates()
    {
        var operatorClient = await ClientAsync(UserRole.Operator);
        await CreateAsync(operatorClient, "ОВ");
        var file = ProfileSerializer.Serialize(new ProfileSet
        {
            Profiles =
            [
                ProfileSerializer.DeserializeProfile(Profile("ОВ").ToJsonString()),
                ProfileSerializer.DeserializeProfile(Profile("ВК").ToJsonString())
            ]
        });

        var result = await (await operatorClient.PostJsonAsync("/api/loi/profiles/import", new { json = file })).ReadJsonAsync<LoiImportView>();

        result.Created.Select(p => p.Name).ShouldBe(["ВК", "ОВ (2)"], ignoreOrder: true);
        (await operatorClient.GetJsonAsync<LoiSummaryView[]>("/api/loi/profiles")).Length.ShouldBe(3);
    }

    [Test]
    public async Task Import_NotAProfilesFile_400()
    {
        var operatorClient = await ClientAsync(UserRole.Operator);

        (await operatorClient.PostJsonAsync("/api/loi/profiles/import", new { json = """{"hello":1}""" })).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Test]
    public async Task Download_IsPluginProfilesFile()
    {
        var operatorClient = await ClientAsync(UserRole.Operator);
        var created = await CreateAsync(operatorClient, "ОВ");

        var text = await operatorClient.GetStringAsync($"/api/loi/profiles/{created.Id}/file");

        ProfileSerializer.Deserialize(text, _ => null).Profiles.ShouldHaveSingleItem().Name.ShouldBe("ОВ");
    }

    [Test]
    public async Task Schedule_Validation_RequiresCompleteProfile()
    {
        var admin = await AdminAsync();
        var sourceId = await ScheduleApi.SourceAsync(admin);
        var draft = await CreateAsync(admin, "Черновик", withRules: false);

        var missing = await admin.PostJsonAsync("/api/schedules", ScheduleBody(sourceId, new { profileId = Guid.NewGuid() }));
        var incomplete = await admin.PostJsonAsync("/api/schedules", ScheduleBody(sourceId, new { profileId = draft.Id }));
        var badHistory = await admin.PostJsonAsync("/api/schedules", ScheduleBody(sourceId, new { profileId = draft.Id, historyFile = @"\\nas\loi\база.txt" }));

        missing.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await missing.Content.ReadAsStringAsync()).ShouldContain("не найден");
        (await incomplete.Content.ReadAsStringAsync()).ShouldContain("нет правил");
        (await badHistory.Content.ReadAsStringAsync()).ShouldContain(".xlsx");
    }

    [Test]
    public async Task Expansion_BuildsLoiJobs_WithScheduleOverrides()
    {
        var admin = await AdminAsync();
        var sourceId = await ScheduleApi.SourceAsync(admin);
        var profile = await CreateAsync(admin, "ОВ");
        var schedule = await ScheduleApi.CreateAsync(admin, ScheduleBody(sourceId,
            new { profileId = profile.Id, viewName = "", problemsOnly = true, historyFile = @"\\nas\loi\{расписание}.xlsx" }));
        await using var agent = await FakeAgent.RegisterAsync(Server, admin);
        agent.ExpandSelection = (_, selection) => selection.Models.Select(m => new ExpandedModelDto(m.Name, m.ModelPath, true, null, m.Rvt, m.Nwc)).ToArray();
        await agent.ConnectAsync();

        var runId = (await (await admin.PostAsync($"/api/schedules/{schedule.Id}/run", null)).ReadJsonAsync<RunCreated>()).RunId;
        await EventuallyAsync(async () => (await DbAsync(db => db.Runs.SingleAsync(r => r.Id == runId))).State != RunState.Expanding);

        var job = (await DbAsync(db => db.Jobs.Where(j => j.RunId == runId).ToListAsync())).ShouldHaveSingleItem();
        job.ModuleKind.ShouldBe(ModuleKinds.Loi);
        job.RequiresNwc.ShouldBeFalse();
        job.RevitYear.ShouldBe(2022);
        var payload = JsonSerializer.Deserialize<LoiJobPayload>(job.PayloadJson, TestJson.Options)!;
        payload.OutputFolder.ShouldStartWith(@"\\nas\loi\LOI ЖК Север\");
        payload.Settings.ViewName.ShouldBe(""); // пустой вид расписания — вся модель, а не вид профиля
        payload.Settings.WorksetKeyword.ShouldBe("#Связи"); // не задан в расписании — как в профиле
        payload.Settings.ProblemsOnly.ShouldBeTrue();
        payload.Settings.HistoryFile.ShouldBe(@"\\nas\loi\LOI ЖК Север.xlsx");
        ProfileSerializer.DeserializeProfile(payload.ProfileJson).Rules.ShouldHaveSingleItem().Title.ShouldBe("Стены");
    }

    [Test]
    public async Task Profile_UsedBySchedule_DeleteConflict()
    {
        var admin = await AdminAsync();
        var sourceId = await ScheduleApi.SourceAsync(admin);
        var profile = await CreateAsync(admin, "ОВ");
        await ScheduleApi.CreateAsync(admin, ScheduleBody(sourceId, new { profileId = profile.Id }));

        (await admin.DeleteAsync($"/api/loi/profiles/{profile.Id}")).StatusCode.ShouldBe(HttpStatusCode.Conflict);
    }

    [Test]
    public async Task Catalog_NoAgents_409()
    {
        var operatorClient = await ClientAsync(UserRole.Operator);

        (await operatorClient.PostJsonAsync("/api/loi/catalog/refresh", new { revitYear = (int?)null })).StatusCode.ShouldBe(HttpStatusCode.Conflict);
    }

    [Test]
    public async Task Catalog_RefreshRunsJob_ResultStored()
    {
        var admin = await AdminAsync();
        await using var agent = await FakeAgent.RegisterAsync(Server, admin);
        await agent.ConnectAsync(new InstalledRevitDto(2024, false));

        var refresh = await admin.PostJsonAsync("/api/loi/catalog/refresh", new { revitYear = (int?)null });
        refresh.EnsureSuccessStatusCode();
        var assignment = (await agent.RequestJobAsync(1, 2024, hasNwcExporter: false)).ShouldNotBeNull();
        assignment.ModuleKind.ShouldBe(ModuleKinds.LoiCatalog);
        JsonSerializer.Deserialize<LoiCatalogPayload>(assignment.PayloadJson, TestJson.Options)!.RevitYear.ShouldBe(2024);

        var result = new LoiCatalogResult([new LoiCategoryDto("OST_Walls", "Стены", true)], ["ADSK_Марка", "Комментарии"], true);
        await agent.CompleteJobAsync(assignment.JobId, JobOutcome.Succeeded, null, JsonSerializer.Serialize(result, TestJson.Options));

        await EventuallyAsync(async () => (await admin.GetAsync("/api/loi/catalog/2024")).IsSuccessStatusCode);
        var catalog = await admin.GetJsonAsync<LoiCatalogDetailsView>("/api/loi/catalog/2024");
        catalog.Catalog.Categories.ShouldHaveSingleItem().Name.ShouldBe("Стены");
        catalog.Catalog.Parameters.ShouldBe(["ADSK_Марка", "Комментарии"]);
        var overview = await admin.GetJsonAsync<LoiCatalogOverviewView>("/api/loi/catalog");
        overview.AgentYears.ShouldBe([2024]);
        overview.Catalogs.ShouldHaveSingleItem().Categories.ShouldBe(1);
        (await DbAsync(db => db.Runs.SingleAsync(r => r.Id == db.Jobs.Single(j => j.Id == assignment.JobId).RunId))).State.ShouldBe(RunState.Completed);
    }

    [Test]
    public async Task Catalog_ResultOver32Kb_Stored()
    {
        var admin = await AdminAsync();
        await using var agent = await FakeAgent.RegisterAsync(Server, admin);
        await agent.ConnectAsync(new InstalledRevitDto(2024, false));
        (await admin.PostJsonAsync("/api/loi/catalog/refresh", new { revitYear = (int?)null })).EnsureSuccessStatusCode();
        var assignment = (await agent.RequestJobAsync(1, 2024, hasNwcExporter: false)).ShouldNotBeNull();

        // каталог настоящей модели — тысячи параметров, больше 32 КБ по умолчанию у SignalR
        var parameters = Enumerable.Range(0, 5000).Select(i => $"ADSK_Параметр_{i}").ToArray();
        var result = new LoiCatalogResult([new LoiCategoryDto("OST_Walls", "Стены", true)], parameters, true);
        await agent.CompleteJobAsync(assignment.JobId, JobOutcome.Succeeded, null, JsonSerializer.Serialize(result, TestJson.Options));

        await EventuallyAsync(async () => (await admin.GetAsync("/api/loi/catalog/2024")).IsSuccessStatusCode);
        (await admin.GetJsonAsync<LoiCatalogDetailsView>("/api/loi/catalog/2024")).Catalog.Parameters.Length.ShouldBe(5000);
    }
}
