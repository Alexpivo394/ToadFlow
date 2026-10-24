using System.Text;
using Alexpivo394.Revit.ParamCheck.Summaries;
using Shouldly;
using ToadFlow.Server.Loi.Naming;
using ToadFlow.Server.Tests.Infrastructure;

namespace ToadFlow.Server.Tests;

public sealed record GroupView(string? Key, string? Customer, int Models, double? FillRate);

public sealed record TrendView(DateOnly Bucket, string? Series, int Checks, double? FillRate);

public sealed record ModelRowView(string ModelKey, string ModelName, string? Object, string? Discipline, double? FillRate, double? PreviousFillRate, bool ProfileChanged);

public sealed record ParameterRowView(string Name, int Models, double? FillRate, string[] WorstModels);

public sealed record OverviewView(int Models, int Checks, int ManualChecks, double? FillRate, GroupView[] ByObject, GroupView[] ByDiscipline, string Granularity,
    TrendView[] Trend, ModelRowView[] WorstModels, ParameterRowView[] WorstParameters);

public sealed record FiltersView(string[] Customers, string[] Disciplines, string[] Profiles);

public sealed record HistoryView(ModelRowView Model, CheckPointView[] Checks, ParameterPointView[] Parameters);

public sealed record CheckPointView(DateTimeOffset CheckedAt, string? UserName, double? FillRate);

public sealed record ParameterPointView(string Rule, string Name, double? FillRate);

public class AnalyticsQueryTests : ServerTest
{
    private static readonly DateTimeOffset Now = DateTimeOffset.UtcNow;

    private static CheckSummary Summary(string model, int filled, int values, DateTimeOffset at, string fingerprint = "v1") => new()
    {
        ModelName = model,
        ModelPath = $"RSN://rs22/{model}.rvt",
        ProfileName = "АР",
        ProfileFingerprint = fingerprint,
        CheckedAt = at,
        Elements = values,
        CompleteElements = filled,
        Values = values,
        Filled = filled,
        Empty = values - filled,
        Rules = [new RuleSummary { Title = "Стены", Elements = values, CompleteElements = filled, Parameters = [new ParameterSummary { Name = "Марка", Filled = filled, Empty = values - filled }] }]
    };

    /// <summary>Полковая АР: была 50 %, стала 90 %; Полковая ВК — 20 %; Донской АР — 100 %, проверен давно.</summary>
    private async Task<HttpClient> SeedAsync()
    {
        var admin = await AdminAsync();
        (await admin.PutJsonAsync("/api/loi/naming", NamingDefaults.Create() with
        {
            Entries =
            [
                ..NamingDefaults.Create().Entries, new(NamingFields.Object, "POL", "Полковая", "ГК Пионер"), new(NamingFields.Object, "DON", "Донской", "ГК Пионер")
            ]
        })).EnsureSuccessStatusCode();

        foreach (var summary in new[]
                 {
                     Summary("POL_AR", 5, 10, Now.AddDays(-10)), Summary("POL_AR", 9, 10, Now.AddDays(-1), "v2"), Summary("POL_VK", 2, 10, Now.AddDays(-2)),
                     Summary("DON_AR", 10, 10, Now.AddDays(-40))
                 })
            (await admin.PostJsonAsync("/api/loi/checks", new { id = Guid.NewGuid(), summary })).EnsureSuccessStatusCode();
        return admin;
    }

    [Test]
    public async Task Overview_StateByLatestCheck_Weighted()
    {
        var admin = await SeedAsync();

        var all = await admin.GetJsonAsync<OverviewView>("/api/loi/analytics/overview");

        all.Models.ShouldBe(3);
        all.Checks.ShouldBe(4);
        all.ManualChecks.ShouldBe(4);
        all.FillRate!.Value.ShouldBe(21.0 / 30, 1e-9); // 9 + 2 + 10 из 30 — по последней проверке каждой модели
        all.ByObject.Single(g => g.Key == "Полковая").FillRate!.Value.ShouldBe(11.0 / 20, 1e-9);
        all.ByObject[0].Key.ShouldBe("Полковая"); // хуже — первым
        all.ByDiscipline.Select(g => g.Key).ShouldBe(["ВК", "АР"]);
        all.WorstModels[0].ModelName.ShouldBe("POL_VK");
        all.WorstParameters.ShouldHaveSingleItem().WorstModels.ShouldBe(["POL_VK", "POL_AR"]);
        all.Trend.Sum(p => p.Checks).ShouldBe(4);
    }

    [Test]
    public async Task Period_And_Filters()
    {
        var admin = await SeedAsync();
        var from = Uri.EscapeDataString(Now.AddDays(-30).ToString("O"));

        var month = await admin.GetJsonAsync<OverviewView>($"/api/loi/analytics/overview?from={from}");
        var vk = await admin.GetJsonAsync<OverviewView>($"/api/loi/analytics/overview?discipline={Uri.EscapeDataString("ВК")}");
        var filters = await admin.GetJsonAsync<FiltersView>("/api/loi/analytics/filters");

        month.Models.ShouldBe(2); // Донской за месяц не проверялся
        month.Granularity.ShouldBe("day");
        vk.Models.ShouldBe(1);
        filters.Customers.ShouldBe(["ГК Пионер"]);
        filters.Disciplines.ShouldBe(["АР", "ВК"], ignoreOrder: true);
    }

    [Test]
    public async Task Models_ShowChangeAndProfileChange_History()
    {
        var admin = await SeedAsync();

        var models = await admin.GetJsonAsync<ModelRowView[]>("/api/loi/analytics/models");
        var ar = models.Single(m => m.ModelName == "POL_AR");
        var history = await admin.GetJsonAsync<HistoryView>($"/api/loi/analytics/model?key={Uri.EscapeDataString(ar.ModelKey)}");

        ar.FillRate.ShouldBe(0.9);
        ar.PreviousFillRate.ShouldBe(0.5);
        ar.ProfileChanged.ShouldBeTrue(); // правила поменялись — рост не обязательно заслуга модели
        history.Checks.Select(c => c.FillRate).ShouldBe([0.5, 0.9]);
        history.Checks[0].UserName.ShouldBe("Администратор");
        history.Parameters.ShouldHaveSingleItem().Rule.ShouldBe("Стены");
    }

    [Test]
    public async Task Csv_ForExcel()
    {
        var admin = await SeedAsync();

        var bytes = await admin.GetByteArrayAsync("/api/loi/analytics/models.csv");
        var text = Encoding.UTF8.GetString(bytes);

        bytes.Take(3).ShouldBe(Encoding.UTF8.GetPreamble());
        text.ShouldContain("Заказчик;Объект;Раздел");
        text.ShouldContain("ГК Пионер;Полковая;АР;;POL_AR;");
        text.ShouldContain(";90,0;90,0;40,0");
    }
}
