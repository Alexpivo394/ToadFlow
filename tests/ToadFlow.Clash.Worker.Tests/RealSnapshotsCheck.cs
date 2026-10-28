using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Alexpivo394.Revit.Clash.Geometry;
using Alexpivo394.Revit.Clash.Rules;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using ToadFlow.Contracts;
using ToadFlow.Contracts.Clash;
using ToadFlow.Server.Clash;
using ToadFlow.Server.Clash.Engine;
using ToadFlow.Server.Clash.Parts;
using ToadFlow.Server.Tests.Infrastructure;

namespace ToadFlow.Clash.Worker.Tests;

/// <summary>
///     Живая проверка на реальных слепках (выгрузки этапа 0 рядом с копиями моделей, папки <c>*.toadclash-*</c>): обычная
///     загрузка агента, часть, черновик «всё против всего» с допуском 20 мм и прогон работника. Только по явному запуску:
///     <c>--treenode-filter "/*/*/RealSnapshotsCheck/*"</c>; папка — <c>TOADCLASH_REAL_WORK</c>, итоги — <c>TOADCLASH_REPORT</c>.
/// </summary>
public class RealSnapshotsCheck : ServerTest
{
    private static readonly Dictionary<string, string> Disciplines = new()
    {
        ["AR"] = "АР", ["EOM"] = "ЭОМ", ["ITP"] = "ИТП", ["KR"] = "КР", ["OVK"] = "ОВК", ["OVO"] = "ОВО", ["OVV"] = "ОВВ", ["PT"] = "ПТ", ["SS"] = "СС",
        ["VK"] = "ВК"
    };

    [Test, Explicit]
    public async Task RealSnapshots_DraftRunWithinBudget()
    {
        var folder = Environment.GetEnvironmentVariable("TOADCLASH_REAL_WORK")
                     ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "ToadClashData", "real-work");
        var snapshots = Directory.GetDirectories(folder, "*.toadclash-*").Order().ToList();
        snapshots.ShouldNotBeEmpty();
        var report = new StringBuilder();
        var admin = await AdminAsync();
        await using var agent = await FakeAgent.RegisterAsync(Server, admin);
        await agent.ConnectAsync("1.8.0", new InstalledRevitDto(2022, true));

        var upload = Stopwatch.StartNew();
        var names = new List<string>();
        foreach (var snapshot in snapshots)
        {
            var name = Path.GetFileName(snapshot)[..Path.GetFileName(snapshot).IndexOf(".toadclash-", StringComparison.Ordinal)] + ".rvt";
            names.Add(name);
            await PublishAsync(agent, name, snapshot);
        }

        report.AppendLine($"Загрузка {names.Count} слепков: {upload.Elapsed.TotalSeconds:0} с");

        var models = await admin.GetJsonAsync<ClashModelView[]>("/api/clash/models");
        var part = await (await admin.PostJsonAsync("/api/clash/parts", new { name = "DON, стилобат" })).ReadJsonAsync<ClashPartView>();
        var members = names.Select(n => new { modelId = models.Single(m => m.Name == n).Id, disciplineOverride = (string?)Disciplines[n.Split('_')[1]] }).ToArray();
        (await admin.PutJsonAsync($"/api/clash/parts/{part.Id}/models", members)).EnsureSuccessStatusCode();
        var disciplines = (await admin.GetJsonAsync<DraftView>($"/api/clash/parts/{part.Id}/draft")).Disciplines;
        var any = new Condition(new PropertyRef(PropertyKind.Category), ConditionOperator.IsNotEmpty, null);
        var config = new ClashConfiguration(disciplines.Select(d => new SelectionSet(Guid.NewGuid(), d, "Все", [new ConditionGroup([any])])).ToList(), [], []);
        (await admin.PutAsync($"/api/clash/parts/{part.Id}/draft", Json(config))).EnsureSuccessStatusCode();
        (await admin.PostAsync($"/api/clash/parts/{part.Id}/draft/matrix/fill", null)).EnsureSuccessStatusCode();

        var preview = Stopwatch.StartNew();
        var single = new ClashConfiguration([config.Sets.Single(s => s.Discipline == "ВК")], [], []);
        var counted = await (await admin.PostAsync($"/api/clash/parts/{part.Id}/preview", Json(single))).ReadJsonAsync<JsonElement>();
        report.AppendLine($"Предпросмотр набора ВК «Все»: {counted.GetProperty("matched")} из {counted.GetProperty("total")}, {preview.Elapsed.TotalSeconds:0.0} с");

        (await admin.PostAsync($"/api/clash/parts/{part.Id}/draft/run", null)).EnsureSuccessStatusCode();
        var cache = Path.Combine(Path.GetTempPath(), "toadflow-engine-cache-real");
        var http = new HttpClient(Server.Server.CreateHandler()) { BaseAddress = new Uri("http://localhost/"), Timeout = TimeSpan.FromMinutes(10) };
        http.DefaultRequestHeaders.Add(EngineApi.KeyHeader, ServerFactory.EngineKey);
        var client = new EngineClient(http);
        var options = new WorkerOptions { CacheRoot = cache, Threads = 8 };
        var worker = new EngineWorker(client, new SnapshotCache(client, options), options, NullLogger<EngineWorker>.Instance);

        var watch = Stopwatch.StartNew();
        (await worker.RunOnceAsync(CancellationToken.None)).ShouldBe(RunOutcome.Succeeded);
        watch.Stop();

        var run = await admin.GetJsonAsync<DraftRunSummary>($"/api/clash/parts/{part.Id}/draft/run");
        var stats = run.Stats.ShouldNotBeNull();
        var summary = await admin.GetJsonAsync<DraftSummary>($"/api/clash/parts/{part.Id}/draft/summary");
        var deepest = await admin.GetJsonAsync<HitPage>($"/api/clash/parts/{part.Id}/draft/hits?sort=depth&pageSize=5");
        var paged = Stopwatch.StartNew();
        await admin.GetJsonAsync<HitPage>($"/api/clash/parts/{part.Id}/draft/hits?page=100&pageSize=50");
        var pageSeconds = paged.Elapsed.TotalSeconds;

        report.AppendLine($"Прогон: {watch.Elapsed.TotalSeconds:0} с (загрузка моделей {stats.LoadSeconds:0} с, поиск {stats.BroadSeconds + stats.NarrowSeconds:0} с), " +
                          $"элементов {stats.Elements:N0}, треугольников {stats.Triangles:N0}, пар-кандидатов {stats.CandidatePairs:N0}, " +
                          $"память работника до {stats.PeakWorkingSetBytes / 1e9:0.0} ГБ");
        report.AppendLine($"Коллизий: {summary.Total:N0}; страница 100 списка — {pageSeconds:0.00} с");
        foreach (var pair in summary.ByDisciplines.OrderByDescending(p => p.Count)) report.AppendLine($"  {pair.DisciplinePair}: {pair.Count:N0}");
        report.AppendLine("Самые глубокие:");
        foreach (var hit in deepest.Items)
            report.AppendLine($"  {hit.DepthMm:0} мм: {hit.ModelNameA} {hit.CategoryA} {hit.ElementIdA} × {hit.ModelNameB} {hit.CategoryB} {hit.ElementIdB}");

        var output = Environment.GetEnvironmentVariable("TOADCLASH_REPORT") ?? Path.Combine(Path.GetTempPath(), "toadclash-real-check.txt");
        await File.WriteAllTextAsync(output, report.ToString());
        Console.WriteLine(report);

        run.State.ShouldBe(EngineRunState.Succeeded);
        summary.Total.ShouldBeGreaterThan(0);
        watch.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(300)); // бюджет спека
    }

    /// <summary>Слепок этапа 0 под ключом модели ToadFlow (ключ входит в TCG), загрузка частями по 8 МиБ, как у агента.</summary>
    private async Task PublishAsync(FakeAgent agent, string name, string snapshot)
    {
        var (jobId, model) = await ClashFixtures.RunningJobAsync(Server, agent, name);
        ModelGeometry geometry;
        await using (var source = File.OpenRead(Path.Combine(snapshot, "model.tcg"))) geometry = ModelGeometryFile.Read(source);
        byte[] tcg;
        using (var target = new MemoryStream())
        {
            ModelGeometryFile.Write(target, new ModelGeometry(geometry.Header with { ModelKey = model.SnapshotKey }, geometry.Elements, geometry.Inventory));
            tcg = target.ToArray();
        }

        var tcp = await File.ReadAllBytesAsync(Path.Combine(snapshot, "model.tcp"));
        var files = new ClashFiles(tcg, tcp, geometry.Elements.Count, 0, geometry.Inventory.Count);
        var http = ClashFixtures.AgentClient(Server, agent);
        var state = await (await http.PostJsonAsync("/api/agent/clash/uploads", files.Request(jobId))).ReadJsonAsync<ClashUploadState>();
        foreach (var (file, bytes) in new[] { (ClashUploads.Geometry, tcg), (ClashUploads.Parameters, tcp) })
            for (var offset = 0; offset < bytes.Length; offset += ClashUploads.MaxChunkBytes)
            {
                var chunk = new ByteArrayContent(bytes, offset, Math.Min(ClashUploads.MaxChunkBytes, bytes.Length - offset));
                (await http.PutAsync($"/api/agent/clash/uploads/{state.UploadId}/{file}?offset={offset}", chunk)).EnsureSuccessStatusCode();
            }

        var completed = await http.PostAsync($"/api/agent/clash/uploads/{state.UploadId}/complete", null);
        completed.EnsureSuccessStatusCode();
        var result = await completed.ReadJsonAsync<ClashExtractResult>();
        await agent.CompleteJobAsync(jobId, JobOutcome.Succeeded, null, JsonSerializer.Serialize(result, TestJson.Options));
    }

    private static StringContent Json(ClashConfiguration config) => new(ConfigurationJson.Serialize(config), Encoding.UTF8, "application/json");
}
