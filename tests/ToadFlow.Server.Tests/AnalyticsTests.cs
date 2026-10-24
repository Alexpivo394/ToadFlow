using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Alexpivo394.Revit.ParamCheck.Summaries;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;
using ToadFlow.Contracts;
using ToadFlow.Server.Data.Entities;
using ToadFlow.Server.Loi.Analytics;
using ToadFlow.Server.Loi.Naming;
using ToadFlow.Server.Tests.Infrastructure;

namespace ToadFlow.Server.Tests;

public sealed record TokenCreatedView(TokenView View, string Token);

public sealed record TokenView(Guid Id, string Name, string Prefix);

public sealed record ManualResultView(bool Recorded, string? Reason);

public sealed record PreviewRowView(string Name, PreviewResultView Result);

public sealed record PreviewResultView(string? Customer, string? Object, string? Discipline, string? Part, string[] Unrecognized);

public class AnalyticsTests : ServerTest
{
    private static readonly NamingConfig Naming = NamingDefaults.Create() with
    {
        Entries =
        [
            ..NamingDefaults.Create().Entries,
            new(NamingFields.Object, "POL", "Полковая", "ГК Пионер"),
            new(NamingFields.Part, "St", "стилобат"),
            new(NamingFields.Ignore, "RLP")
        ]
    };

    private static CheckSummary Summary(string model = "POL_AR_RLP_St_R22", int filled = 3) => new()
    {
        ModelName = model,
        ModelPath = $"RSN://rs22/POL/{model}.rvt",
        ProfileName = "АР",
        ProfileFingerprint = "abc",
        CheckedAt = DateTimeOffset.UtcNow,
        Elements = 2,
        CompleteElements = 1,
        Values = 4,
        Filled = filled,
        Empty = 4 - filled,
        Rules =
        [
            new RuleSummary
            {
                Title = "Стены", Elements = 2, CompleteElements = 1,
                Parameters = [new ParameterSummary { Name = "Марка", Filled = 2 }, new ParameterSummary { Name = "Код", Filled = filled - 2, Empty = 4 - filled }]
            }
        ]
    };

    private async Task<HttpClient> TokenClientAsync(HttpClient user, string name = "ParamChecker")
    {
        var created = await (await user.PostJsonAsync("/api/auth/tokens", new { name })).EnsureSuccessStatusCode().ReadJsonAsync<TokenCreatedView>();
        created.Token.ShouldStartWith("tf_");
        var client = Server.CreateClient();
        client.DefaultRequestHeaders.Add("X-ToadFlow-Token", created.Token);
        return client;
    }

    private Task<List<LoiCheck>> ChecksAsync() => DbAsync(db => db.Set<LoiCheck>().Include(c => c.Rules).ThenInclude(r => r.Parameters).AsNoTracking().ToListAsync());

    private async Task SetNamingAsync() => (await (await AdminAsync()).PutJsonAsync("/api/loi/naming", Naming)).EnsureSuccessStatusCode();

    [Test]
    public async Task Token_ActsAsItsUser_CannotManageTokens_Revocable()
    {
        var viewer = await ClientAsync(UserRole.Viewer);
        var token = await TokenClientAsync(viewer);

        (await token.GetAsync("/api/auth/me")).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await token.GetAsync("/api/users")).StatusCode.ShouldBe(HttpStatusCode.Forbidden); // права те же, что у наблюдателя
        (await token.PostJsonAsync("/api/auth/tokens", new { name = "ещё" })).StatusCode.ShouldBe(HttpStatusCode.Forbidden);

        var id = (await viewer.GetJsonAsync<TokenView[]>("/api/auth/tokens")).ShouldHaveSingleItem().Id;
        (await viewer.DeleteAsync($"/api/auth/tokens/{id}")).EnsureSuccessStatusCode();
        (await token.GetAsync("/api/auth/me")).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Test]
    public async Task Token_OfDisabledUser_Rejected()
    {
        var viewer = await ClientAsync(UserRole.Viewer);
        var token = await TokenClientAsync(viewer);
        await DbAsync(db => db.Users.Where(u => u.Role == UserRole.Viewer).ExecuteUpdateAsync(u => u.SetProperty(x => x.IsDisabled, true)));

        (await token.GetAsync("/api/auth/me")).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Test]
    public async Task ManualCheck_RecordedOnce_Classified()
    {
        await SetNamingAsync();
        var token = await TokenClientAsync(await ClientAsync(UserRole.Viewer));
        var request = new { id = Guid.NewGuid(), summary = Summary() };

        var first = await (await token.PostJsonAsync("/api/loi/checks", request)).EnsureSuccessStatusCode().ReadJsonAsync<ManualResultView>();
        var again = await (await token.PostJsonAsync("/api/loi/checks", request)).ReadJsonAsync<ManualResultView>();

        first.Recorded.ShouldBeTrue();
        again.Recorded.ShouldBeFalse(); // плагин отправил повторно после обрыва — не задваивается
        var check = (await ChecksAsync()).ShouldHaveSingleItem();
        check.Source.ShouldBe(LoiCheckSource.Manual);
        check.UserId.ShouldNotBeNull();
        (check.Customer, check.Object, check.Discipline, check.Part).ShouldBe(("ГК Пионер", "Полковая", "АР", "стилобат"));
        check.ModelKey.ShouldBe("rsn://rs22/pol/pol_ar_rlp_st_r22.rvt");
        check.Rules.ShouldHaveSingleItem().Parameters.Select(p => p.Name).ShouldBe(["Марка", "Код"], ignoreOrder: true);
    }

    [Test]
    public async Task AnalyticsDisabled_NothingRecorded()
    {
        var admin = await AdminAsync();
        (await admin.PutJsonAsync("/api/loi/analytics/settings", new { enabled = false, retentionYears = 0 })).EnsureSuccessStatusCode();

        var result = await (await admin.PostJsonAsync("/api/loi/checks", new { id = Guid.NewGuid(), summary = Summary() })).ReadJsonAsync<ManualResultView>();

        result.Recorded.ShouldBeFalse();
        result.Reason.ShouldNotBeNull();
        (await ChecksAsync()).ShouldBeEmpty();
    }

    [Test]
    public async Task ScheduleJob_ResultRecorded()
    {
        var admin = await AdminAsync();
        await using var agent = await FakeAgent.RegisterAsync(Server, admin);
        await agent.ConnectAsync();
        var jobId = await DbAsync(async db =>
        {
            var run = Seed.Run(db);
            var job = Seed.Job(db, run.Id);
            job.ModuleKind = ModuleKinds.Loi;
            await db.SaveChangesAsync();
            return job.Id;
        });
        (await agent.RequestJobAsync(1, 2022)).ShouldNotBeNull();

        await agent.CompleteJobAsync(jobId, JobOutcome.Succeeded, null, JsonSerializer.Serialize(Summary(), TestJson.Options));

        await EventuallyAsync(async () => (await ChecksAsync()).Count == 1);
        var check = (await ChecksAsync()).Single();
        check.Id.ShouldBe(jobId);
        check.Source.ShouldBe(LoiCheckSource.Schedule);
        check.RunId.ShouldNotBeNull();
        check.Filled.ShouldBe(3);
    }

    [Test]
    public async Task ScheduleWithAnalyticsOff_NotRecorded()
    {
        var admin = await AdminAsync();
        await using var agent = await FakeAgent.RegisterAsync(Server, admin);
        await agent.ConnectAsync();
        var jobId = await DbAsync(async db =>
        {
            var schedule = Seed.Schedule(db, Seed.Source(db).Id);
            schedule.ModuleKind = ModuleKinds.Loi;
            schedule.ModuleSettingsJson = $$"""{"profileId":"{{Guid.NewGuid()}}","analytics":false}""";
            var run = Seed.Run(db, scheduleId: schedule.Id);
            var job = Seed.Job(db, run.Id);
            job.ModuleKind = ModuleKinds.Loi;
            await db.SaveChangesAsync();
            return job.Id;
        });
        (await agent.RequestJobAsync(1, 2022)).ShouldNotBeNull();

        await agent.CompleteJobAsync(jobId, JobOutcome.Succeeded, null, JsonSerializer.Serialize(Summary(), TestJson.Options));

        await EventuallyAsync(async () => (await DbAsync(db => db.Jobs.SingleAsync(j => j.Id == jobId))).Status == JobStatus.Succeeded);
        await Task.Delay(300);
        (await ChecksAsync()).ShouldBeEmpty();
    }

    [Test]
    public async Task Naming_PreviewWithUnsavedConfig_AndValidation()
    {
        var admin = await AdminAsync();

        var rows = await (await admin.PostJsonAsync("/api/loi/naming/preview", new { config = Naming, names = new[] { "POL_AR_RLP_St_R22", "XXX_AR" } }))
            .EnsureSuccessStatusCode().ReadJsonAsync<PreviewRowView[]>();
        var broken = await admin.PutJsonAsync("/api/loi/naming", Naming with { Patterns = [new(NamingFields.Part, "K(\\d+", "корпус $1")] });

        rows[0].Result.Object.ShouldBe("Полковая");
        rows[1].Result.Unrecognized.ShouldBe(["XXX"]);
        broken.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await (await ClientAsync(UserRole.Operator)).PutJsonAsync("/api/loi/naming", Naming)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Test]
    public async Task Override_And_Reclassify_UpdateRecordedChecks()
    {
        var admin = await AdminAsync();
        await admin.PostJsonAsync("/api/loi/checks", new { id = Guid.NewGuid(), summary = Summary() });
        (await ChecksAsync()).Single().Object.ShouldBeNull(); // справочник ещё без объекта POL

        await SetNamingAsync();
        (await admin.PostAsync("/api/loi/analytics/reclassify", null)).EnsureSuccessStatusCode();
        (await ChecksAsync()).Single().Object.ShouldBe("Полковая");

        var summary = Summary();
        (await admin.PutJsonAsync("/api/loi/models/overrides", new { modelPath = summary.ModelPath, modelName = summary.ModelName, @object = "Полковая (стилобат)" }))
            .EnsureSuccessStatusCode();
        var check = (await ChecksAsync()).Single();
        check.Object.ShouldBe("Полковая (стилобат)");
        check.Discipline.ShouldBe("АР"); // не заданное в поправке — по справочнику

        (await admin.DeleteAsync($"/api/loi/models/overrides?key={Uri.EscapeDataString(check.ModelKey)}")).EnsureSuccessStatusCode();
        (await ChecksAsync()).Single().Object.ShouldBe("Полковая");
    }

    [Test]
    public async Task Retention_DeletesOldChecks_AndViewFlattensParameters()
    {
        var admin = await AdminAsync();
        var old = Summary("POL_AR_RLP_S1_R22");
        old.CheckedAt = DateTimeOffset.UtcNow.AddYears(-3);
        await admin.PostJsonAsync("/api/loi/checks", new { id = Guid.NewGuid(), summary = old });
        await admin.PostJsonAsync("/api/loi/checks", new { id = Guid.NewGuid(), summary = Summary() });

        var rows = await DbAsync(db => db.Database.SqlQuery<int>($"SELECT count(*)::int AS \"Value\" FROM loi_analytics").SingleAsync());
        rows.ShouldBe(4); // 2 проверки × 1 правило × 2 параметра

        (await admin.PutJsonAsync("/api/loi/analytics/settings", new { enabled = true, retentionYears = 2 })).EnsureSuccessStatusCode();
        await Server.Services.GetServices<IHostedService>().OfType<AnalyticsCleaner>().Single().CleanAsync(CancellationToken.None);

        (await ChecksAsync()).ShouldHaveSingleItem().ModelName.ShouldBe("POL_AR_RLP_St_R22");
        (await DbAsync(db => db.Set<LoiCheckParameter>().CountAsync())).ShouldBe(2);
    }
}
