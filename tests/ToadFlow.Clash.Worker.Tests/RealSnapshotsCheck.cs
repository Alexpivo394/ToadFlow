using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Alexpivo394.Revit.Clash.Geometry;
using Alexpivo394.Revit.Clash.Rules;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using ToadFlow.Contracts;
using ToadFlow.Contracts.Clash;
using ToadFlow.Server.Clash;
using ToadFlow.Server.Clash.Collisions;
using ToadFlow.Server.Clash.Engine;
using ToadFlow.Server.Clash.Parts;
using ToadFlow.Server.Clash.Viewer;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
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
        var report = new StringBuilder();
        var (admin, partId) = await PreparePartAsync(report);
        var elapsed = await RunWorkerAsync(admin, partId);
        await ReportRunAsync(admin, partId, elapsed, report);
    }

    /// <summary>
    ///     Просмотрщик на реальной части: время и размер сцены и сеток для самых глубоких коллизий и для коллизий с самым
    ///     плотным окружением. С <c>TOADCLASH_HOLD_MINUTES</c> сервер остаётся доступен на http://localhost:5000 (вход
    ///     <c>admin</c> / <c>admin-pass</c>) — веб открывается через <c>npm run dev</c>.
    /// </summary>
    [Test, Explicit]
    public async Task RealSnapshots_ViewerScenes()
    {
        var report = new StringBuilder();
        var (admin, partId) = await PreparePartAsync(report);
        await RunWorkerAsync(admin, partId);

        var deepest = (await admin.GetJsonAsync<HitPage>($"/api/clash/parts/{partId}/draft/hits?sort=depth&pageSize=3")).Items;
        var ordinary = (await admin.GetJsonAsync<HitPage>($"/api/clash/parts/{partId}/draft/hits?page=200&pageSize=40")).Items;
        var dense = (await admin.GetJsonAsync<HitPage>($"/api/clash/parts/{partId}/draft/hits?disciplines={Uri.EscapeDataString("ОВО × ОВО")}&pageSize=40")).Items;
        var measured = new List<(DraftHitView Hit, ViewerSceneView Scene, double SceneSeconds, double MeshSeconds, long Bytes)>();
        foreach (var hit in deepest.Concat(ordinary).Concat(dense))
        {
            var watch = Stopwatch.StartNew();
            var scene = await admin.GetJsonAsync<ViewerSceneView>($"/api/clash/runs/{hit.RunId}/hits/{hit.Number}/scene");
            var sceneSeconds = watch.Elapsed.TotalSeconds;
            watch.Restart();
            var bytes = await admin.GetByteArrayAsync($"/api/clash/runs/{hit.RunId}/hits/{hit.Number}/meshes");
            measured.Add((hit, scene, sceneSeconds, watch.Elapsed.TotalSeconds, bytes.Length));
        }

        report.AppendLine($"Просмотрщик, {measured.Count} коллизий (самые глубокие, обычные, ОВО × ОВО):");
        foreach (var m in measured.Take(3).Concat(measured.OrderByDescending(m => m.Scene.MeshTriangles).Take(5)).Distinct())
            report.AppendLine($"  № {m.Hit.Number} {m.Hit.DisciplineA} × {m.Hit.DisciplineB}: элементов {m.Scene.Elements.Count}, с сеткой {m.Scene.Elements.Count(e => e.Mesh)}, " +
                              $"треугольников {m.Scene.MeshTriangles:N0}{(m.Scene.Truncated ? ", урезано" : "")}; сцена {m.SceneSeconds:0.00} с, " +
                              $"сетки {m.MeshSeconds:0.00} с, {m.Bytes / 1e6:0.0} МБ");
        report.AppendLine($"  Медиана: сцена {Median(measured.Select(m => m.SceneSeconds)):0.00} с, сетки {Median(measured.Select(m => m.MeshSeconds)):0.00} с, " +
                          $"{Median(measured.Select(m => m.Bytes / 1e6)):0.0} МБ; максимум сеток {measured.Max(m => m.MeshSeconds):0.00} с, " +
                          $"{measured.Max(m => m.Bytes) / 1e6:0.0} МБ");
        await WriteReportAsync(report);

        if (int.TryParse(Environment.GetEnvironmentVariable("TOADCLASH_HOLD_MINUTES"), out var minutes) && minutes > 0)
            await HoldAsync(TimeSpan.FromMinutes(minutes));

        measured.ShouldAllBe(m => m.Scene.Elements.Count >= 2 && m.Bytes > 8);
    }

    /// <summary>
    ///     Цикл утверждённой проверки на реальной части: утверждение и прогон; повтор без новых выгрузок (номера те же,
    ///     пары не пересчитываются); новая версия с допуском 50 мм («ушла из-за правил»); выгрузка ОВК, где удалены элементы
    ///     открытых коллизий («исправлена»). С <c>TOADCLASH_HOLD_MINUTES</c> сервер остаётся для показа.
    /// </summary>
    [Test, Explicit]
    public async Task RealSnapshots_ApprovedCycle()
    {
        var report = new StringBuilder();
        var (admin, partId) = await PreparePartAsync(report);
        var worker = Worker();

        async Task<(ApprovedRunView Run, TimeSpan Elapsed)> RunAsync(string label)
        {
            var watch = Stopwatch.StartNew();
            (await worker.RunOnceAsync(CancellationToken.None)).ShouldBe(RunOutcome.Succeeded);
            var elapsed = watch.Elapsed;
            var run = await admin.GetJsonAsync<ApprovedRunView>($"/api/clash/parts/{partId}/approved/run");
            var s = run.Summary.ShouldNotBeNull();
            var stats = run.Run.Stats.ShouldNotBeNull();
            report.AppendLine($"{label}: {elapsed.TotalSeconds:0} с (поиск {stats.BroadSeconds + stats.NarrowSeconds:0.0} с, точная фаза {stats.NarrowSeconds:0.0} с), " +
                              $"найдено {s.Found:N0}: новых {s.New:N0}, активных {s.Active:N0}, вернулись {s.Returned:N0}, исправлено {s.Fixed:N0}, " +
                              $"ушли из-за правил {s.GoneByRules:N0}, не проверено {s.Unchecked:N0}; без пересчёта {s.ReusedPairs:N0} из {stats.CandidatePairs:N0} пар");
            return (run, elapsed);
        }

        (await admin.PostJsonAsync($"/api/clash/parts/{partId}/approve", new { comment = "всё против всего, 20 мм", run = true })).EnsureSuccessStatusCode();
        var first = await RunAsync("v1, первый прогон");
        await RequestImagesAsync(admin, partId, report);
        await ReportV1Async(admin, first.Run.Run.RunId, report);
        var numbers = (await admin.GetJsonAsync<CollisionPage>($"/api/clash/parts/{partId}/approved/collisions?status=all&pageSize=200")).Items
            .ToDictionary(c => (c.ModelA, c.UniqueIdA, c.ModelB, c.UniqueIdB), c => c.Number);

        (await admin.PostAsync($"/api/clash/parts/{partId}/approved/run", null)).EnsureSuccessStatusCode();
        var second = await RunAsync("v1, повтор без новых выгрузок");
        var again = (await admin.GetJsonAsync<CollisionPage>($"/api/clash/parts/{partId}/approved/collisions?status=all&pageSize=200")).Items;
        foreach (var c in again) numbers[(c.ModelA, c.UniqueIdA, c.ModelB, c.UniqueIdB)].ShouldBe(c.Number);
        second.Run.Summary!.New.ShouldBe(0);
        second.Run.Summary.ReusedPairs.ShouldBe(second.Run.Run.Stats!.CandidatePairs);

        var draft = ConfigurationJson.Deserialize((await admin.GetJsonAsync<DraftView>($"/api/clash/parts/{partId}/draft")).Configuration.GetRawText());
        (await admin.PutAsync($"/api/clash/parts/{partId}/draft", Json(draft with { Matrix = draft.Matrix.Select(c => c with { ToleranceMm = 50 }).ToList() })))
            .EnsureSuccessStatusCode();
        (await admin.PostJsonAsync($"/api/clash/parts/{partId}/approve", new { comment = "допуск 50 мм", run = true })).EnsureSuccessStatusCode();
        var third = await RunAsync("v2, допуск 50 мм");
        third.Run.Summary!.GoneByRules.ShouldBeGreaterThan(0);
        third.Run.Summary.Fixed.ShouldBe(0);

        // Новая выгрузка ОВК: удалены элементы ОВК из 100 открытых коллизий.
        var ovk = (await admin.GetJsonAsync<ClashPartView>($"/api/clash/parts/{partId}")).Models.Single(m => m.Name.Contains("_OVK_"));
        var key = ovk.ModelId.ToString("N");
        var open = (await admin.GetJsonAsync<CollisionPage>($"/api/clash/parts/{partId}/approved/collisions?model={key}&pageSize=100")).Items;
        var removed = open.Select(c => c.ModelA == key ? c.UniqueIdA : c.UniqueIdB).ToHashSet();
        var folder = Directory.GetDirectories(Environment.GetEnvironmentVariable("TOADCLASH_REAL_WORK")
                                              ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "ToadClashData", "real-work"),
            "*_OVK_*.toadclash-*").Single();
        await using (var agent = await FakeAgent.RegisterAsync(Server, admin))
        {
            await agent.ConnectAsync("1.8.0", new InstalledRevitDto(2022, true));
            await using var heartbeat = HeartbeatLoop.Start(agent);
            await PublishAsync(agent, ovk.Name, folder, removed);
        }

        (await admin.PostAsync($"/api/clash/parts/{partId}/approved/run", null)).EnsureSuccessStatusCode();
        var fourth = await RunAsync($"v2, ОВК без {removed.Count} элементов");
        fourth.Run.Summary!.Fixed.ShouldBeGreaterThan(0);
        report.AppendLine($"Повтор быстрее первого прогона в {first.Elapsed / second.Elapsed:0.0} раза");

        // Этап 5: рабочие места по ролям — «Мои» проектировщика ОВК, сводка и обзор координатора на всей части.
        var all = (await admin.GetJsonAsync<CollisionPage>($"/api/clash/parts/{partId}/approved/collisions?status=all&pageSize=1")).Total;
        await UserAsync("ovk", "Проектировщик ОВК", UserRole.Designer, ["ОВК"]);
        var ovkClient = await LoginAsync("ovk", UserPassword);
        report.AppendLine($"Этап 5, часть DON, коллизий всего {all:N0}:");
        async Task<T> TimedAsync<T>(HttpClient client, string label, string url)
        {
            await client.GetJsonAsync<T>(url); // прогрев
            var watch = Stopwatch.StartNew();
            var result = await client.GetJsonAsync<T>(url);
            report.AppendLine($"  {label}: {watch.Elapsed.TotalSeconds:0.00} с");
            watch.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(2), label);
            return result;
        }

        var my = await TimedAsync<MyCollisionPage>(ovkClient, "«Мои» ОВК, первая страница", "/api/clash/my");
        var mySummary = await TimedAsync<MySummary>(ovkClient, "«Мои», счётчики", "/api/clash/my/summary");
        report.AppendLine($"    мне править {mySummary.ToFix:N0}, взяла другая сторона {mySummary.TakenByOther:N0}, новых {mySummary.NewSinceVisit:N0}; на странице {my.Items.Count}");
        var analytics = await TimedAsync<ToadFlow.Server.Clash.Analytics.ClashAnalyticsView>(admin, "Сводка без фильтров", "/api/clash/analytics");
        await TimedAsync<ToadFlow.Server.Clash.Analytics.ClashAnalyticsView>(admin, "Сводка по разделу ОВК", "/api/clash/analytics?discipline=%D0%9E%D0%92%D0%9A");
        report.AppendLine($"    активных {analytics.Current.Open:N0}, за период: добавилось {analytics.Period.New:N0}, исправлено {analytics.Period.Fixed:N0}; " +
                          $"отстают: {string.Join(", ", analytics.Lagging.Take(3).Select(l => $"{l.Key} {l.Open:N0}"))}");
        await TimedAsync<ToadFlow.Server.Clash.Overview.OverviewView>(admin, "Обзор координатора", "/api/clash/overview");
        await Stage8Async(admin, ovkClient, partId, report);
        await WriteReportAsync(report);

        if (int.TryParse(Environment.GetEnvironmentVariable("TOADCLASH_HOLD_MINUTES"), out var minutes) && minutes > 0)
            await HoldAsync(TimeSpan.FromMinutes(minutes));
    }

    /// <summary>Пользователь с паролем тестов; уже есть — не трогается.</summary>
    private Task UserAsync(string login, string name, UserRole role, string[] disciplines, bool pending = false) => DbAsync(async db =>
    {
        if (db.Users.Any(u => u.Login == login)) return;
        var user = ToadFlow.Server.Data.Entities.User.Create(login, name, role, "", DateTimeOffset.UtcNow);
        user.PasswordHash = new Microsoft.AspNetCore.Identity.PasswordHasher<ToadFlow.Server.Data.Entities.User>().HashPassword(user, UserPassword);
        user.Disciplines = disciplines;
        user.IsPending = pending;
        db.Users.Add(user);
        await db.SaveChangesAsync();
    });

    private static double Median(IEnumerable<double> values)
    {
        var sorted = values.Order().ToArray();
        return sorted[sorted.Length / 2];
    }

    /// <summary>
    ///     Прокси на http://localhost:5000 к серверу теста в памяти: так к нему ходит веб из <c>npm run dev</c>. Хабы SignalR
    ///     не проксируются — живых обновлений в браузере не будет.
    /// </summary>
    private async Task HoldAsync(TimeSpan duration)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://localhost:5000");
        await using var app = builder.Build();
        var inner = new HttpClient(Server.Server.CreateHandler()) { BaseAddress = new Uri("http://localhost/"), Timeout = TimeSpan.FromMinutes(5) };
        app.Map("/{**path}", async (HttpContext context) =>
        {
            using var request = new HttpRequestMessage(new HttpMethod(context.Request.Method), context.Request.Path + context.Request.QueryString);
            if (context.Request.ContentLength > 0 || context.Request.Headers.ContainsKey("Transfer-Encoding"))
            {
                request.Content = new StreamContent(context.Request.Body);
                if (context.Request.ContentType is { } type) request.Content.Headers.TryAddWithoutValidation("Content-Type", type);
            }

            if (context.Request.Headers.Authorization.ToString() is { Length: > 0 } auth) request.Headers.TryAddWithoutValidation("Authorization", auth);
            // Личный токен ToadTools (плагины Revit) — свой заголовок.
            if (context.Request.Headers[ToadFlow.Server.Auth.ApiTokenAuthHandler.Header].ToString() is { Length: > 0 } token)
                request.Headers.TryAddWithoutValidation(ToadFlow.Server.Auth.ApiTokenAuthHandler.Header, token);
            using var response = await inner.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, context.RequestAborted);
            context.Response.StatusCode = (int)response.StatusCode;
            if (response.Content.Headers.ContentType is { } contentType) context.Response.ContentType = contentType.ToString();
            await response.Content.CopyToAsync(context.Response.Body, context.RequestAborted);
        });
        // Пользователи для показа, пароль тестов: coord2 — второй координатор (совместная работа двумя браузерами), ovk —
        // проектировщик ОВК, boss — наблюдатель (руководитель), newbie — заявка, ждущая подтверждения.
        await UserAsync("coord2", "Координатор 2", UserRole.Operator, []);
        await UserAsync("ovk", "Проектировщик ОВК", UserRole.Designer, ["ОВК"]);
        await UserAsync("boss", "Руководитель", UserRole.Viewer, []);
        await UserAsync("newbie", "Новый проектировщик", UserRole.Designer, ["ВК"], pending: true);

        await app.StartAsync();
        Console.WriteLine($"Сервер с реальной частью: http://localhost:5000 до {DateTime.Now + duration:HH:mm}");
        // Пока сервер открыт, работник берёт прогоны из очереди — «Прогнать» из веба считается.
        using var hold = new CancellationTokenSource(duration);
        var worker = Worker();
        while (!hold.IsCancellationRequested)
        {
            try
            {
                if (await worker.RunOnceAsync(hold.Token) is null) await Task.Delay(2000, hold.Token);
            }
            catch (OperationCanceledException)
            {
            }
        }

        await app.StopAsync();
    }

    /// <summary>
    ///     Отчёт первого прогона: время шапки и первой страницы «изменений», выгрузка Excel по всей части и HTML по паре ОВК × КР
    ///     (время, размер; файлы — в <c>%TEMP%\toadclash-report-sample</c>), текст для чата.
    /// </summary>
    private static async Task ReportV1Async(HttpClient admin, Guid runId, StringBuilder report)
    {
        const string web = "http://toadflow.local";
        var watch = Stopwatch.StartNew();
        var view = await admin.GetJsonAsync<ToadFlow.Server.Clash.Reports.ReportView>($"/api/clash/reports/{runId}");
        var header = watch.Elapsed;
        watch.Restart();
        var page = await admin.GetJsonAsync<ToadFlow.Server.Clash.Reports.ReportPage>($"/api/clash/reports/{runId}/collisions?scope=changes&pageSize=50");
        var firstPage = watch.Elapsed;
        report.AppendLine($"Отчёт v1: шапка {header.TotalSeconds:0.00} с, первая страница изменений {firstPage.TotalSeconds:0.00} с (строк {page.Total:N0}); " +
                          $"пары: {string.Join(", ", view.Pairs.OrderByDescending(p => p.Open).Take(3).Select(p => $"{p.Pair} {p.Open:N0}"))}");

        var sample = Path.Combine(Path.GetTempPath(), "toadclash-report-sample");
        Directory.CreateDirectory(sample);
        async Task<(TimeSpan Elapsed, long Bytes)> DownloadAsync(string url, string file)
        {
            var timer = Stopwatch.StartNew();
            using var response = await admin.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
            response.EnsureSuccessStatusCode();
            await using (var output = File.Create(Path.Combine(sample, file)))
                await response.Content.CopyToAsync(output);
            return (timer.Elapsed, new FileInfo(Path.Combine(sample, file)).Length);
        }

        var xlsx = await DownloadAsync($"/api/clash/reports/{runId}/export.xlsx?scope=changes&baseUrl={web}", "report-v1.xlsx");
        report.AppendLine($"  Excel по всей части ({page.Total:N0} строк): {xlsx.Elapsed.TotalSeconds:0.0} с, {xlsx.Bytes / 1024.0 / 1024:0.0} МБ");
        var pair = view.Pairs.First(p => p.Pair.Contains("ОВК") && p.Pair.Contains("КР")).Pair;
        var html = await DownloadAsync($"/api/clash/reports/{runId}/export.html?scope=changes&pair={Uri.EscapeDataString(pair)}&baseUrl={web}", "report-v1-ovk-kr.html");
        var pairRows = view.Pairs.First(p => p.Pair == pair);
        report.AppendLine($"  HTML по паре {pair} ({pairRows.New:N0} новых): {html.Elapsed.TotalSeconds:0.0} с, {html.Bytes / 1024.0 / 1024:0.0} МБ");
        var chat = await admin.GetJsonAsync<ToadFlow.Server.Clash.Reports.ChatMessageView>($"/api/clash/reports/{runId}/chat?baseUrl={web}");
        report.AppendLine("  Текст для чата:");
        foreach (var line in chat.Text.Split('\n')) report.AppendLine($"    {line}");

        await ReportLayoutAsync(admin, runId, view, report, DownloadAsync);
    }

    /// <summary>
    ///     Отчёт после показа: страница по умолчанию (100 строк — вся часть и раздел ОВК), уровни, HTML самой большой пары в
    ///     «Открытых сейчас» (время, размер, картинок в файле), архив «всё» в «Открытых сейчас» (время, размер, пар, сумма
    ///     <c>min(1000, n)</c> картинок) и его повтор — картинки уже в кеше. Файлы — в <c>%TEMP%\toadclash-report-sample</c>.
    /// </summary>
    private static async Task ReportLayoutAsync(HttpClient admin, Guid runId, ToadFlow.Server.Clash.Reports.ReportView view, StringBuilder report,
        Func<string, string, Task<(TimeSpan Elapsed, long Bytes)>> download)
    {
        const string web = "http://toadflow.local";
        async Task<(T Value, TimeSpan Elapsed)> TimedAsync<T>(string url)
        {
            var timer = Stopwatch.StartNew();
            var value = await admin.GetJsonAsync<T>(url);
            return (value, timer.Elapsed);
        }

        var all = await TimedAsync<ToadFlow.Server.Clash.Reports.ReportPage>($"/api/clash/reports/{runId}/collisions?scope=changes&pageSize=100");
        var ovk = await TimedAsync<ToadFlow.Server.Clash.Reports.ReportPage>(
            $"/api/clash/reports/{runId}/collisions?scope=changes&pageSize=100&discipline={Uri.EscapeDataString("ОВК")}");
        var levels = await TimedAsync<List<ToadFlow.Server.Clash.Reports.ReportLevelView>>($"/api/clash/reports/{runId}/levels?scope=changes");
        report.AppendLine($"Отчёт по разделам: страница 100 строк — вся часть {all.Elapsed.TotalSeconds:0.00} с, раздел ОВК {ovk.Elapsed.TotalSeconds:0.00} с " +
                          $"({ovk.Value.Total:N0} строк); уровни {levels.Elapsed.TotalSeconds:0.00} с ({levels.Value.Count:N0} групп пара × уровень)");

        var largest = view.Pairs.OrderByDescending(p => p.Open).First();
        var html = await download($"/api/clash/reports/{runId}/export.html?scope=open&pair={Uri.EscapeDataString(largest.Pair)}&baseUrl={web}",
            "report-largest-pair.html");
        var images = CountImages(await File.ReadAllTextAsync(Path.Combine(Path.GetTempPath(), "toadclash-report-sample", "report-largest-pair.html")));
        report.AppendLine($"  HTML самой большой пары {largest.Label} ({largest.Open:N0} открытых, картинок {images:N0}): " +
                          $"{html.Elapsed.TotalSeconds:0.0} с, {html.Bytes / 1024.0 / 1024:0.0} МБ");

        var expected = view.Pairs.Where(p => p.Open > 0).Sum(p => Math.Min(ToadFlow.Server.Clash.Reports.ReportHtml.MaxImages, p.Open));
        foreach (var label in new[] { "архив «всё»", "повтор архива (картинки в кеше)" })
        {
            var timer = Stopwatch.StartNew();
            var start = await admin.PostAsync($"/api/clash/reports/{runId}/archive?scope=open&baseUrl={web}", null);
            start.EnsureSuccessStatusCode();
            ToadFlow.Server.Clash.Reports.ReportArchiveView state;
            do
            {
                await Task.Delay(1000);
                state = await admin.GetJsonAsync<ToadFlow.Server.Clash.Reports.ReportArchiveView>("/api/clash/report-archives/current");
            } while (state.State is ToadFlow.Server.Clash.Reports.ReportArchives.Queued or ToadFlow.Server.Clash.Reports.ReportArchives.Running);
            state.State.ShouldBe(ToadFlow.Server.Clash.Reports.ReportArchives.Ready, state.Error);
            var built = timer.Elapsed;
            var zip = await download($"/api/clash/report-archives/{state.Id}/download", "report-all-open.zip");
            report.AppendLine($"  {label}, «Открытые сейчас»: собран за {built.TotalSeconds:0} с, скачан за {zip.Elapsed.TotalSeconds:0.0} с, " +
                              $"{zip.Bytes / 1024.0 / 1024:0} МБ, пар {state.PairsTotal:N0}, картинок {expected:N0}");
        }
    }

    private static int CountImages(string html)
    {
        var count = 0;
        for (var at = html.IndexOf("data:image/", StringComparison.Ordinal); at >= 0; at = html.IndexOf("data:image/", at + 1, StringComparison.Ordinal)) count++;
        return count;
    }

    /// <summary>
    ///     Картинки по запросу после первого прогона: до 200 коллизий части (по номеру; шаг выборки не больше 300,
    ///     при малом числе коллизий меньше). Первые 20 — по одной (у первой холодный кеш оглавлений), затем те же 20 повторно (из кеша),
    ///     остальные — параллельно по 50 (последняя пачка меньше), как страница отчёта; картинки пачек рисуются с нуля. Пишет время первой картинки, p50 и p95
    ///     по одной, время пачки из 50 и пик рабочего набора процесса (выборка раз в 200 мс). Цели (в отчёт, не в утверждения):
    ///     p95 тёплой картинки не больше 300 мс, пачка из 50 не больше 3 с. Пять картинок — в <c>%TEMP%\toadclash-images-sample</c>.
    /// </summary>
    private static async Task RequestImagesAsync(HttpClient admin, Guid partId, StringBuilder report)
    {
        const int sampleSize = 200, single = 20, batch = 50, step = 300, pageSize = 200;
        var total = (await admin.GetJsonAsync<CollisionPage>($"/api/clash/parts/{partId}/approved/collisions?status=all&pageSize=1")).Total;
        var stride = Math.Max(1, Math.Min(step, total / sampleSize));
        var pages = new Dictionary<int, IReadOnlyList<CollisionRow>>();
        var picked = new List<CollisionRow>();
        for (var index = 0; index < total && picked.Count < sampleSize; index += stride)
        {
            var page = index / pageSize + 1;
            if (!pages.TryGetValue(page, out var items))
                pages[page] = items = (await admin.GetJsonAsync<CollisionPage>($"/api/clash/parts/{partId}/approved/collisions?status=all&pageSize={pageSize}&page={page}")).Items;
            if (index % pageSize < items.Count) picked.Add(items[index % pageSize]);
        }

        picked = picked.OrderBy(c => c.Number).ToList();
        var process = Process.GetCurrentProcess();
        long peak = 0;
        using var sampling = new CancellationTokenSource();
        var sampler = Task.Run(async () =>
        {
            while (!sampling.IsCancellationRequested)
            {
                process.Refresh();
                peak = Math.Max(peak, process.WorkingSet64);
                try { await Task.Delay(200, sampling.Token); } catch (OperationCanceledException) { }
            }
        });

        var sampleFolder = Path.Combine(Path.GetTempPath(), "toadclash-images-sample");
        if (Directory.Exists(sampleFolder)) Directory.Delete(sampleFolder, true);
        Directory.CreateDirectory(sampleFolder);
        var failures = new List<string>();
        long bytes = 0, images = 0;

        async Task<(TimeSpan Elapsed, bool Ok)> RequestAsync(CollisionRow row)
        {
            var watch = Stopwatch.StartNew();
            using var response = await admin.GetAsync($"/api/clash/collisions/{row.Id}/image?v={row.ImageVersion}");
            var data = await response.Content.ReadAsByteArrayAsync();
            var elapsed = watch.Elapsed;
            if (response.IsSuccessStatusCode)
            {
                Interlocked.Add(ref bytes, data.Length);
                Interlocked.Increment(ref images);
                if (Directory.GetFiles(sampleFolder).Length < 5) File.WriteAllBytes(Path.Combine(sampleFolder, $"{row.Number}.png"), data);
            }
            else
            {
                lock (failures) failures.Add($"№ {row.Number}: {(int)response.StatusCode}");
            }

            return (elapsed, response.IsSuccessStatusCode);
        }

        var cold = new List<double>();
        foreach (var row in picked.Take(single))
        {
            var (elapsed, ok) = await RequestAsync(row);
            if (ok) cold.Add(elapsed.TotalMilliseconds);
        }

        // Тёплые: повтор тех же адресов, когда файлы уже в кеше.
        var warm = new List<double>();
        foreach (var row in picked.Take(single))
        {
            var (elapsed, ok) = await RequestAsync(row);
            if (ok) warm.Add(elapsed.TotalMilliseconds);
        }

        var batches = new List<double>();
        foreach (var chunk in picked.Skip(single).Chunk(batch))
        {
            var watch = Stopwatch.StartNew();
            await Task.WhenAll(chunk.Select(RequestAsync));
            batches.Add(watch.Elapsed.TotalSeconds);
        }

        await sampling.CancelAsync();
        await sampler;

        static double Percentile(List<double> values, double p) =>
            values.Count == 0 ? 0 : values.Order().ElementAt(Math.Min(values.Count - 1, (int)Math.Ceiling(values.Count * p) - 1));

        report.AppendLine($"Картинки по запросу v1: {picked.Count} из {total:N0} коллизий (каждая {stride}-я); первая (холодный кеш оглавлений) {(cold.Count > 0 ? cold[0] / 1000 : 0):0.00} с; " +
                          $"по одной (успешных {cold.Count}) p50 {Percentile(cold, 0.5):0} мс, p95 {Percentile(cold, 0.95):0} мс; повтор из кеша p95 {Percentile(warm, 0.95):0} мс (цель ≤ 300); " +
                          $"пачки до {batch} (рисуются с нуля, последняя меньше):{string.Join(", ", batches.Select(b => $"{b:0.0} с"))} (цель ≤ 3 с); PNG в среднем {bytes / 1024.0 / Math.Max(1, images):0.0} КБ; " +
                          $"пик рабочего набора процесса {peak / 1024.0 / 1024 / 1024:0.00} ГБ; ошибок {failures.Count}{(failures.Count > 0 ? " (" + string.Join(", ", failures.Take(5)) + ")" : "")}");
    }

    private EngineWorker Worker()
    {
        var cache = Path.Combine(Path.GetTempPath(), "toadflow-engine-cache-real");
        var http = new HttpClient(Server.Server.CreateHandler()) { BaseAddress = new Uri("http://localhost/"), Timeout = TimeSpan.FromMinutes(10) };
        http.DefaultRequestHeaders.Add(EngineApi.KeyHeader, ServerFactory.EngineKey);
        var client = new EngineClient(http);
        var options = new WorkerOptions { CacheRoot = cache, Threads = 8 };
        return new EngineWorker(client, new SnapshotCache(client, options), options, NullLogger<EngineWorker>.Instance);
    }

    private async Task<(HttpClient Admin, Guid PartId)> PreparePartAsync(StringBuilder report)
    {
        var folder = Environment.GetEnvironmentVariable("TOADCLASH_REAL_WORK")
                     ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "ToadClashData", "real-work");
        var snapshots = Directory.GetDirectories(folder, "*.toadclash-*").Order().ToList();
        snapshots.ShouldNotBeEmpty();
        var admin = await AdminAsync();
        await using var agent = await FakeAgent.RegisterAsync(Server, admin);
        await agent.ConnectAsync("1.8.0", new InstalledRevitDto(2022, true));
        await using var heartbeat = HeartbeatLoop.Start(agent);

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
        var config = ConfigurationDefaults.FromDisciplines(disciplines.Select(d => new DisciplineSet(Guid.NewGuid(), d, "Все", [new ConditionGroup([any])])).ToList(), [], []);
        (await admin.PutAsync($"/api/clash/parts/{part.Id}/draft", Json(config))).EnsureSuccessStatusCode();
        (await admin.PostAsync($"/api/clash/parts/{part.Id}/draft/matrix/fill", null)).EnsureSuccessStatusCode();

        var preview = Stopwatch.StartNew();
        var single = config with { Sets = [config.Sets.Single(s => s.GroupId == ConfigurationDefaults.DisciplineGroupId("ВК"))] };
        var counted = await (await admin.PostAsync($"/api/clash/parts/{part.Id}/preview", Json(single))).ReadJsonAsync<JsonElement>();
        report.AppendLine($"Предпросмотр набора ВК «Все»: {counted.GetProperty("matched")} из {counted.GetProperty("total")}, {preview.Elapsed.TotalSeconds:0.0} с");
        return (admin, part.Id);
    }

    private async Task<TimeSpan> RunWorkerAsync(HttpClient admin, Guid partId)
    {
        (await admin.PostAsync($"/api/clash/parts/{partId}/draft/run", null)).EnsureSuccessStatusCode();
        var cache = Path.Combine(Path.GetTempPath(), "toadflow-engine-cache-real");
        var http = new HttpClient(Server.Server.CreateHandler()) { BaseAddress = new Uri("http://localhost/"), Timeout = TimeSpan.FromMinutes(10) };
        http.DefaultRequestHeaders.Add(EngineApi.KeyHeader, ServerFactory.EngineKey);
        var client = new EngineClient(http);
        var options = new WorkerOptions { CacheRoot = cache, Threads = 8 };
        var worker = new EngineWorker(client, new SnapshotCache(client, options), options, NullLogger<EngineWorker>.Instance);

        var watch = Stopwatch.StartNew();
        (await worker.RunOnceAsync(CancellationToken.None)).ShouldBe(RunOutcome.Succeeded);
        return watch.Elapsed;
    }

    private async Task ReportRunAsync(HttpClient admin, Guid partId, TimeSpan elapsed, StringBuilder report)
    {
        var run = await admin.GetJsonAsync<DraftRunSummary>($"/api/clash/parts/{partId}/draft/run");
        var stats = run.Stats.ShouldNotBeNull();
        var summary = await admin.GetJsonAsync<DraftSummary>($"/api/clash/parts/{partId}/draft/summary");
        var deepest = await admin.GetJsonAsync<HitPage>($"/api/clash/parts/{partId}/draft/hits?sort=depth&pageSize=5");
        var paged = Stopwatch.StartNew();
        await admin.GetJsonAsync<HitPage>($"/api/clash/parts/{partId}/draft/hits?page=100&pageSize=50");
        var pageSeconds = paged.Elapsed.TotalSeconds;

        report.AppendLine($"Прогон: {elapsed.TotalSeconds:0} с (загрузка моделей {stats.LoadSeconds:0} с, поиск {stats.BroadSeconds + stats.NarrowSeconds:0} с), " +
                          $"элементов {stats.Elements:N0}, треугольников {stats.Triangles:N0}, пар-кандидатов {stats.CandidatePairs:N0}, " +
                          $"память работника до {stats.PeakWorkingSetBytes / 1e9:0.0} ГБ");
        report.AppendLine($"Коллизий: {summary.Total:N0}; страница 100 списка — {pageSeconds:0.00} с");
        foreach (var check in summary.Checks.Where(c => c.Count > 0).OrderByDescending(c => c.Count)) report.AppendLine($"  {check.GroupAName} × {check.GroupBName}: {check.Count:N0}");
        report.AppendLine("Самые глубокие:");
        foreach (var hit in deepest.Items)
            report.AppendLine($"  {hit.DepthMm:0} мм: {hit.ModelNameA} {hit.CategoryA} {hit.ElementIdA} × {hit.ModelNameB} {hit.CategoryB} {hit.ElementIdB}");

        await WriteReportAsync(report);

        run.State.ShouldBe(EngineRunState.Succeeded);
        summary.Total.ShouldBeGreaterThan(0);
        elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(300)); // бюджет спека
    }

    /// <summary>
    ///     Этап 8 на реальной части: время фасетов, групп по элементу (проверка и «Мои»), массовых действий, сцены набора и
    ///     предпросмотра утверждения; шум черновика → исключение пары наборов → прогон черновика. Все замеры пишутся в отчёт,
    ///     превышения цели собираются и проверяются в конце.
    /// </summary>
    private async Task Stage8Async(HttpClient admin, HttpClient ovk, Guid partId, StringBuilder report)
    {
        report.AppendLine("Этап 8:");
        var slow = new List<string>();
        var part = $"/api/clash/parts/{partId}";

        async Task<JsonElement> TimedAsync(string label, double limitSeconds, Func<Task<HttpResponseMessage>> call, bool warm = true)
        {
            if (warm) (await call()).EnsureSuccessStatusCode();
            var watch = Stopwatch.StartNew();
            using var response = await call();
            var bytes = await response.Content.ReadAsByteArrayAsync();
            var elapsed = watch.Elapsed;
            response.EnsureSuccessStatusCode();
            report.AppendLine($"  {label}: {elapsed.TotalSeconds:0.00} с (цель ≤ {limitSeconds:0} с)");
            if (elapsed.TotalSeconds > limitSeconds) slow.Add($"{label}: {elapsed.TotalSeconds:0.00} с");
            return response.Content.Headers.ContentType?.MediaType == "application/json" ? JsonDocument.Parse(bytes).RootElement.Clone() : default;
        }

        StringContent Body(string json) => new(json, Encoding.UTF8, "application/json");
        async Task<string> DraftJsonAsync() => (await admin.GetJsonAsync<DraftView>($"{part}/draft")).Configuration.GetRawText();

        var total = (await admin.GetJsonAsync<CollisionPage>($"{part}/approved/collisions?pageSize=1")).Total;
        report.AppendLine($"  открытых коллизий части: {total:N0}");
        await TimedAsync("фасеты проверки", 1, () => admin.GetAsync($"{part}/approved/facets?status=all"));
        var groups = await TimedAsync("группы по элементу ОВК", 1,
            () => admin.GetAsync($"{part}/approved/collisions?status=all&group=element&side={Uri.EscapeDataString("ОВК")}&pageSize=50"));
        report.AppendLine($"    групп {groups.GetProperty("groups").GetInt32():N0}, коллизий в них {groups.GetProperty("collisions").GetInt32():N0}");
        await TimedAsync("«Мои» ОВК по элементу", 1, () => ovk.GetAsync("/api/clash/my?group=element&pageSize=50"));
        await TimedAsync("«Мои» ОВК, фасеты", 1, () => ovk.GetAsync("/api/clash/my/facets"));

        // Массовые действия: 200 — «фиктивная» с примечанием, 2 000 — «кто правит».
        var ids = new List<Guid>();
        for (var page = 1; ids.Count < 2200; page++)
        {
            var items = (await admin.GetJsonAsync<CollisionPage>($"{part}/approved/collisions?sort=number&pageSize=200&page={page}")).Items;
            if (items.Count == 0) break;
            ids.AddRange(items.Select(c => c.Id));
        }

        var fake = await TimedAsync("массово «фиктивная», 200", 5, () => admin.PostJsonAsync("/api/clash/collisions/bulk",
            new { partId, action = "Fake", note = "проверка этапа 8", ids = ids.Take(200).ToList() }), warm: false);
        fake.GetProperty("done").GetInt32().ShouldBe(200);
        var assign = await TimedAsync("массово «кто правит», 2 000", 5, () => admin.PostJsonAsync("/api/clash/collisions/bulk",
            new { partId, action = "Assignee", assignee = "SideA", ids = ids.Skip(200).Take(2000).ToList() }), warm: false);
        report.AppendLine($"    изменено {assign.GetProperty("done").GetInt32():N0}, отказов {assign.GetProperty("refused").GetArrayLength():N0}");

        // Набор в 3D: самый большой набор черновика.
        var draftJson = await DraftJsonAsync();
        var sets = JsonDocument.Parse(draftJson).RootElement.GetProperty("sets").EnumerateArray()
            .Select(s => (Id: s.GetProperty("id").GetString()!, Name: s.GetProperty("name").GetString()!)).ToList();
        var counts = new List<(string Id, string Name, int Matched)>();
        foreach (var set in sets)
        {
            using var counted = await admin.PostAsync($"{part}/preview", Body($"{{\"configuration\":{draftJson},\"setId\":\"{set.Id}\"}}"));
            counts.Add((set.Id, set.Name, (await counted.ReadJsonAsync<JsonElement>()).GetProperty("matched").GetInt32()));
        }

        var biggest = counts.MaxBy(c => c.Matched);
        var sceneBody = $"{{\"configuration\":{draftJson},\"setId\":\"{biggest.Id}\"}}";
        var scene = await TimedAsync($"сцена набора «{biggest.Name}» ({biggest.Matched:N0} элементов)", 3,
            () => admin.PostAsync($"{part}/draft/sets/scene", Body(sceneBody)));
        report.AppendLine($"    в сцене {scene.GetProperty("elements").GetArrayLength():N0}, урезана: {scene.GetProperty("truncated").GetBoolean()}, " +
                          $"треугольников {scene.GetProperty("meshTriangles").GetInt64():N0}");
        await TimedAsync("сетки сцены набора", 10, () => admin.PostAsync($"{part}/draft/sets/meshes", Body(sceneBody)));

        // Шум черновика → правило: самая шумная пара наборов самой шумной проверки — в исключения, прогон черновика заново.
        var drafted = await RunWorkerAsync(admin, partId);
        report.AppendLine($"  прогон черновика: {drafted.TotalSeconds:0} с");
        var check = (await admin.GetJsonAsync<JsonElement>($"{part}/draft/summary")).GetProperty("checks").EnumerateArray()
            .MaxBy(c => c.GetProperty("count").GetInt32());
        var key = check.GetProperty("key").GetString()!;
        var noise = (await admin.GetJsonAsync<JsonElement>($"{part}/draft/checks/{key}/setpairs")).EnumerateArray()
            .MaxBy(p => p.GetProperty("count").GetInt32());
        var (setA, setB) = (noise.GetProperty("setA").GetString()!, noise.GetProperty("setB").GetString()!);
        report.AppendLine($"  шум: «{noise.GetProperty("setAName").GetString()} × {noise.GetProperty("setBName").GetString()}» — " +
                          $"{noise.GetProperty("count").GetInt32():N0} из {check.GetProperty("count").GetInt32():N0} в проверке");
        var fresh = await TimedAsync("предпросмотр утверждения", 1, () => admin.GetAsync($"{part}/approve/preview"));
        fresh.GetProperty("stale").GetBoolean().ShouldBeFalse();

        var config = ConfigurationJson.Deserialize(draftJson);
        var excluded = config with { Exclusions = [.. config.Exclusions, new SetPairExclusion(Guid.Parse(setA), Guid.Parse(setB))] };
        (await admin.PutAsync($"{part}/draft", Json(excluded))).EnsureSuccessStatusCode();
        var stale = await TimedAsync("предпросмотр, черновик изменён после прогона", 1, () => admin.GetAsync($"{part}/approve/preview"));
        stale.GetProperty("stale").GetBoolean().ShouldBeTrue();
        report.AppendLine($"    изменений в настройке: {stale.GetProperty("changes").GetArrayLength()}");

        await RunWorkerAsync(admin, partId);
        var after = (await admin.GetJsonAsync<JsonElement>($"{part}/draft/checks/{key}/setpairs")).EnumerateArray()
            .Where(p => p.GetProperty("setA").GetString() == setA && p.GetProperty("setB").GetString() == setB)
            .Sum(p => p.GetProperty("count").GetInt32());
        after.ShouldBe(0);
        var preview = await TimedAsync("предпросмотр после прогона", 1, () => admin.GetAsync($"{part}/approve/preview"));
        var pairs = preview.GetProperty("pairs").EnumerateArray().ToList();
        report.AppendLine($"    если утвердить: уйдут ≈ {pairs.Sum(p => p.GetProperty("gone").GetInt32()):N0}, " +
                          $"появятся ≈ {pairs.Sum(p => p.GetProperty("new").GetInt32()):N0}, " +
                          $"без изменений {pairs.Sum(p => p.GetProperty("same").GetInt32()):N0} ({pairs.Count} пар разделов)");

        await WriteReportAsync(report);
        slow.ShouldBeEmpty();
    }

    private static async Task WriteReportAsync(StringBuilder report)
    {
        var output = Environment.GetEnvironmentVariable("TOADCLASH_REPORT") ?? Path.Combine(Path.GetTempPath(), "toadclash-real-check.txt");
        await File.WriteAllTextAsync(output, report.ToString());
        Console.WriteLine(report);
    }

    /// <summary>Слепок этапа 0 под ключом модели ToadFlow (ключ входит в TCG), загрузка частями по 8 МиБ, как у агента.</summary>
    /// <param name="removed">Элементы, которых в этой выгрузке нет ни в геометрии, ни в описи, — как удалённые в Revit; выгрузка — новая, сейчас.</param>
    private async Task PublishAsync(FakeAgent agent, string name, string snapshot, IReadOnlySet<string>? removed = null)
    {
        var (jobId, model) = await ClashFixtures.RunningJobAsync(Server, agent, name);
        ModelGeometry geometry;
        await using (var source = File.OpenRead(Path.Combine(snapshot, "model.tcg"))) geometry = ModelGeometryFile.Read(source);
        byte[] tcg;
        using (var target = new MemoryStream())
        {
            if (removed is not null)
                geometry = new ModelGeometry(geometry.Header with { ExtractedAt = DateTimeOffset.UtcNow }, geometry.Elements.Where(e => !removed.Contains(e.Info.UniqueId)).ToList(),
                    geometry.Inventory.Where(id => !removed.Contains(id)).ToList());
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

    /// <summary>
    ///     Heartbeat тестового агента, пока идут долгие загрузки реальных слепков: без него сервер через 30 с тишины считает
    ///     агента потерявшим связь и рвёт соединение.
    /// </summary>
    private sealed class HeartbeatLoop : IAsyncDisposable
    {
        private readonly CancellationTokenSource _stop = new();
        private Task _loop = Task.CompletedTask;

        public static HeartbeatLoop Start(FakeAgent agent)
        {
            var heartbeat = new HeartbeatLoop();
            heartbeat._loop = Task.Run(async () =>
            {
                try
                {
                    while (true)
                    {
                        await agent.HeartbeatAsync();
                        await Task.Delay(TimeSpan.FromSeconds(10), heartbeat._stop.Token);
                    }
                }
                catch (OperationCanceledException)
                {
                }
            });
            return heartbeat;
        }

        public async ValueTask DisposeAsync()
        {
            await _stop.CancelAsync();
            await _loop;
            _stop.Dispose();
        }
    }
}
