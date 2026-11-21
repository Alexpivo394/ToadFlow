using System.Diagnostics;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ToadFlow.Agent.Config;
using ToadFlow.Agent.Server;
using ToadFlow.Contracts;
using ToadFlow.Contracts.Families;
using ToadFlow.Server.Data;
using ToadFlow.Server.Families.Cards;
using ToadFlow.Server.Families.Data;
using ToadFlow.Server.Families.Read;
using ToadFlow.Server.Families.Search;
using ToadFlow.Server.Runs;
using ToadFlow.Server.Tests.Infrastructure;

namespace ToadFlow.Agent.Tests.Families;

/// <summary>
///     Проверка на настоящей папке семейств, вручную: настоящий агент (ядро, без Revit) обходит копию библиотеки и отправляет отчёты
///     серверу по сети, сервер применяет их, итоги чтения в Revit подставляются из прогона аддина. <c>TF_REAL_ROOT</c> — копия
///     библиотеки (в неё пишутся <c>.toadflow</c> и правки), <c>TF_REAL_READ</c> — папка с итогами чтения, <c>TF_REAL_REPORT</c> —
///     куда писать отчёт. <c>TF_REAL_KEEP</c> — не останавливать сервер (порт 5000), пока не появится этот файл: смотреть веб.
/// </summary>
[Explicit]
public sealed class RealLibraryCheck
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };
    private const string Url = "http://localhost:5000";

    [ClassDataSource<PostgresFixture>(Shared = SharedType.PerTestSession)]
    public required PostgresFixture Postgres { get; init; }

    private readonly StringBuilder _report = new();

    private void Line(string text)
    {
        _report.AppendLine(text);
        Console.WriteLine(text);
    }

    [Test]
    [Timeout(4 * 60 * 60 * 1000)]
    public async Task RealLibrary(CancellationToken ct)
    {
        var root = Environment.GetEnvironmentVariable("TF_REAL_ROOT") ?? throw new InvalidOperationException("TF_REAL_ROOT");
        var reads = Environment.GetEnvironmentVariable("TF_REAL_READ");
        var reportPath = Environment.GetEnvironmentVariable("TF_REAL_REPORT") ?? Path.Combine(root, "..", "real-check.md");
        var data = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "toadflow-real-agent-" + Guid.NewGuid().ToString("N"))).FullName;
        AgentPaths.Override = data; // не %ProgramData%: там конфигурация установленного агента

        var connection = await Postgres.CreateDatabaseAsync();
        await using var server = new ServerFactory(connection, services: FamiliesFixtures.WithoutScanLoop);
        server.UseKestrel(5000);
        server.StartServer();
        var admin = await LoginAsync("admin", ServerFactory.AdminPassword);

        try
        {
            await using var agent = await StartAgentAsync(admin);
            (await admin.PutAsJsonAsync("/api/families/settings", new { root, reviewFolder = "_На проверке", scanIntervalMinutes = 10, libraryAgentId = (Guid?)null }, Json))
                .EnsureSuccessStatusCode();

            Line($"# Проверка на настоящей папке — {DateTime.Now:yyyy-MM-dd HH:mm}");
            Line($"Библиотека: `{root}`");
            Line("");
            Line("## 1. Первый обход");
            var first = await ScanAsync(admin, server, ct);
            await StatsAsync(server, admin);

            Line("");
            Line("## 2. Повторный обход без изменений");
            var before = await CountAsync<FamilyVersion>(server);
            var second = await ScanAsync(admin, server, ct);
            var after = await CountAsync<FamilyVersion>(server);
            Line($"- версий до и после: {before} → {after} (новых быть не должно); обход {second.TotalSeconds:0.0} с против {first.TotalSeconds:0.0} с первого");

            Line("");
            Line("## 3. Правки проводником");
            await ChangesAsync(root, admin, server, ct);

            if (reads is not null && File.Exists(Path.Combine(reads, "results.jsonl")))
            {
                Line("");
                Line("## 4. Чтение фермой (итоги аддина в Revit 2022)");
                await ReadAsync(server, admin, reads, ct);
                Line("");
                Line("## 5. Поиск");
                await SearchAsync(admin);
            }

            await File.WriteAllTextAsync(reportPath, _report.ToString(), new UTF8Encoding(false), ct);
            if (Environment.GetEnvironmentVariable("TF_REAL_KEEP") is { } keep)
            {
                Console.WriteLine($"Сервер на {Url}; остановится, когда появится {keep}");
                while (!File.Exists(keep)) await Task.Delay(2000, ct);
            }
        }
        finally
        {
            AgentPaths.Override = null;
        }
    }

    private static async Task<HttpClient> LoginAsync(string login, string password)
    {
        var client = new HttpClient { BaseAddress = new Uri(Url), Timeout = TimeSpan.FromMinutes(5) };
        var response = await client.PostAsJsonAsync("/api/auth/login", new { login, password });
        response.EnsureSuccessStatusCode();
        var token = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("token").GetString();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    /// <summary>Ядро агента новой версии без Revit: только обход библиотеки.</summary>
    private static async Task<AgentHost> StartAgentAsync(HttpClient admin)
    {
        var code = (await (await admin.PostAsync("/api/agents/registration-codes", null)).Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString()!;
        var config = await new Registration().RegisterAsync(Url, code, "BIM-LIB-REAL");
        var services = new ServiceCollection();
        services.AddLogging(logging => logging.SetMinimumLevel(LogLevel.Warning));
        services.AddToadFlowAgentCore(config, new AgentOptions { MachineName = "BIM-LIB-REAL", AgentVersion = "2.5.0", Revits = [], Transport = HttpTransportType.WebSockets });
        var provider = services.BuildServiceProvider();
        var hosted = provider.GetServices<IHostedService>().ToList();
        foreach (var service in hosted) await service.StartAsync(CancellationToken.None);
        var connection = provider.GetRequiredService<ServerConnection>();
        var deadline = DateTime.UtcNow.AddSeconds(60);
        while (!connection.IsConnected && DateTime.UtcNow < deadline) await Task.Delay(200);
        return new AgentHost(provider, hosted);
    }

    private sealed class AgentHost(ServiceProvider provider, List<IHostedService> hosted) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            foreach (var service in hosted) await service.StopAsync(CancellationToken.None);
            await provider.DisposeAsync();
        }
    }

    private async Task<TimeSpan> ScanAsync(HttpClient admin, ServerFactory server, CancellationToken ct)
    {
        var watch = Stopwatch.StartNew();
        var started = await admin.PostAsync("/api/families/scan", null, ct);
        if (!started.IsSuccessStatusCode) throw new InvalidOperationException(await started.Content.ReadAsStringAsync(ct));
        var scanId = (await started.Content.ReadFromJsonAsync<JsonElement>(ct)).GetProperty("scanId").GetGuid();
        FamilyScan? scan = null;
        while (watch.Elapsed < TimeSpan.FromMinutes(60))
        {
            scan = await DbAsync(server, db => db.Set<FamilyScan>().AsNoTracking().SingleAsync(s => s.Id == scanId));
            if (scan.FinishedAt is not null) break;
            await Task.Delay(1000, ct);
        }

        Line($"- обход {watch.Elapsed.TotalSeconds:0.0} с: файлов в отчёте {scan!.Files}, полный — {(scan.Complete == true ? "да" : "нет")}, ошибок {scan.Errors.Length}");
        foreach (var error in scan.Errors.Take(10)) Line($"  - ошибка: {error}");
        return watch.Elapsed;
    }

    private async Task StatsAsync(ServerFactory server, HttpClient admin)
    {
        var cards = await DbAsync(server, db => db.Set<FamilyCard>().AsNoTracking().GroupBy(c => c.State).Select(g => new { g.Key, Count = g.Count() }).ToListAsync());
        Line($"- карточки: {string.Join(", ", cards.Select(c => $"{c.Key} {c.Count}"))}");
        var issues = await DbAsync(server, db => db.Set<FamilyIssue>().AsNoTracking().Where(i => i.ResolvedAt == null).GroupBy(i => i.Kind).Select(g => new { g.Key, Count = g.Count() }).ToListAsync());
        Line($"- случаи «Нужно решение»: {(issues.Count == 0 ? "нет" : string.Join(", ", issues.Select(i => $"{i.Key} {i.Count}")))}");
        var sample = await DbAsync(server, db => db.Set<FamilyIssue>().AsNoTracking().Where(i => i.ResolvedAt == null).OrderBy(i => i.Kind).Take(3).ToListAsync());
        foreach (var issue in sample) Line($"  - {issue.Kind}: {issue.Text}");
        var years = await DbAsync(server, db => db.Set<FamilyCard>().AsNoTracking().GroupBy(c => c.RevitYear).Select(g => new { g.Key, Count = g.Count() }).OrderBy(g => g.Key).ToListAsync());
        Line($"- версии Revit по файлам: {string.Join(", ", years.Select(y => $"{y.Key?.ToString() ?? "не прочитана"} — {y.Count}"))}");
        var history = Path.Combine(Environment.GetEnvironmentVariable("TF_REAL_ROOT")!, ".toadflow", "history");
        Line($"- копий в .toadflow\\history: {(Directory.Exists(history) ? Directory.GetFiles(history).Length : 0)}");
        var status = await admin.GetFromJsonAsync<JsonElement>("/api/families/status");
        Line($"- состояние: агент {status.GetProperty("agentName")}, в сети {status.GetProperty("agentOnline")}, ждут чтения {status.GetProperty("pendingReads")}");
    }

    private async Task ChangesAsync(string root, HttpClient admin, ServerFactory server, CancellationToken ct)
    {
        var files = Directory.GetFiles(root, "*.rfa", SearchOption.AllDirectories).Where(f => !f.Contains(@"\.toadflow\") && !f.Contains(@"\_На проверке\"))
            .Where(f => !System.Text.RegularExpressions.Regex.IsMatch(f, @"\.\d{4}\.rfa$"))
            .OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToList();
        var cards = await DbAsync(server, db => db.Set<FamilyCard>().AsNoTracking().Where(c => c.State == CardState.Approved).ToListAsync());
        string Rel(string full) => full[(root.TrimEnd('\\').Length + 1)..];
        FamilyCard CardOf(string full) => cards.Single(c => c.RelativePath.Equals(Rel(full), StringComparison.OrdinalIgnoreCase));
        var owned = files.Where(f => cards.Any(c => c.RelativePath.Equals(Rel(f), StringComparison.OrdinalIgnoreCase))).ToList();

        var overwritten = owned[3];
        var renamed = owned[10];
        var reviewed = owned[20];
        var deleted = owned[30];
        var copied = owned[40];
        var (overwriteCard, renameCard, reviewCard, deleteCard, copyCard) = (CardOf(overwritten), CardOf(renamed), CardOf(reviewed), CardOf(deleted), CardOf(copied));

        // Перезапись: новое содержимое, только что записано — обход ждёт, пока файл не перестанет меняться.
        await File.AppendAllTextAsync(overwritten, "перезапись", ct);
        var renamedTo = Path.Combine(Path.GetDirectoryName(renamed)!, "Переименованное " + Path.GetFileName(renamed));
        File.Move(renamed, renamedTo);
        var review = Directory.CreateDirectory(Path.Combine(root, "_На проверке")).FullName;
        File.Move(reviewed, Path.Combine(review, Path.GetFileName(reviewed)));
        File.Delete(deleted);
        var otherFolder = Directory.CreateDirectory(Path.Combine(root, "Копии")).FullName;
        File.Copy(copied, Path.Combine(otherFolder, Path.GetFileName(copied)));
        var conflict = Path.Combine(otherFolder, Path.GetFileName(owned[50]));
        File.Copy(owned[60], conflict); // другое содержимое под именем существующей карточки
        File.SetLastWriteTimeUtc(conflict, DateTime.UtcNow.AddHours(-1));

        await ScanAsync(admin, server, ct);
        var midway = await DbAsync(server, db => db.Set<FamilyVersion>().AsNoTracking().CountAsync(v => v.CardId == overwriteCard.Id));
        Line($"- перезаписанный только что файл: версий после первого обхода {midway} (ждёт, пока допишется)");
        await ScanAsync(admin, server, ct);

        var now = await DbAsync(server, db => db.Set<FamilyCard>().AsNoTracking().ToDictionaryAsync(c => c.Id));
        var versions = await DbAsync(server, db => db.Set<FamilyVersion>().AsNoTracking().Where(v => v.CardId == overwriteCard.Id).OrderBy(v => v.SeenAt).ToListAsync());
        Line($"- перезапись `{Rel(overwritten)}`: версий {versions.Count}, у новой пометка «{versions[^1].Note}»");
        Line($"- переименование: карточка та же — {now[renameCard.Id].RelativePath == Rel(renamedTo)}, имя «{now[renameCard.Id].Name}», состояние {now[renameCard.Id].State}");
        Line($"- в папку проверки: состояние {now[reviewCard.Id].State}, путь `{now[reviewCard.Id].RelativePath}`");
        Line($"- удаление: состояние {now[deleteCard.Id].State}");
        await StatsAsync(server, admin);
        var history = Path.Combine(root, ".toadflow", "history");
        Line($"- обе версии перезаписанного файла в истории: {versions.All(v => File.Exists(Path.Combine(history, v.Hash + ".rfa")))}");
        var issues = await DbAsync(server, db => db.Set<FamilyIssue>().AsNoTracking().Where(i => i.ResolvedAt == null && i.RelativePath.StartsWith("Копии")).ToListAsync());
        Line($"- копия в другой папке и чужое содержимое под занятым именем: {string.Join(", ", issues.Select(i => $"{i.Kind} `{i.RelativePath}`"))}; второй карточки нет — {now.Values.Count(c => c.NameKey == copyCard.NameKey && c.State != CardState.Archived) == 1}");

        // Вернуть удалённый файл из истории — карточка возвращается из архива.
        var deletedVersion = await DbAsync(server, db => db.Set<FamilyVersion>().AsNoTracking().FirstAsync(v => v.CardId == deleteCard.Id));
        File.Copy(Path.Combine(history, deletedVersion.Hash + ".rfa"), deleted);
        File.SetLastWriteTimeUtc(deleted, DateTime.UtcNow.AddHours(-1));
        await ScanAsync(admin, server, ct);
        var restored = await DbAsync(server, db => db.Set<FamilyCard>().AsNoTracking().SingleAsync(c => c.Id == deleteCard.Id));
        Line($"- файл вернули из .toadflow\\history: состояние {restored.State}");
    }

    /// <summary>Агент фермы «читает» семейства: берёт задания и сдаёт итоги, снятые аддином в настоящем Revit 2022.</summary>
    private async Task ReadAsync(ServerFactory server, HttpClient admin, string reads, CancellationToken ct)
    {
        var results = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in await File.ReadAllLinesAsync(Path.Combine(reads, "results.jsonl"), ct))
        {
            var fields = line.Split('\t');
            results[fields[0]] = fields;
        }

        var code = (await (await admin.PostAsync("/api/agents/registration-codes", null, ct)).Content.ReadFromJsonAsync<JsonElement>(ct)).GetProperty("code").GetString()!;
        var config = await new Registration().RegisterAsync(Url, code, "BIM-FARM-REAL", ct);
        await using var hub = new HubConnectionBuilder()
            .WithUrl($"{Url}/hubs/agent", options =>
            {
                options.Headers["X-Agent-Id"] = config.AgentId.ToString();
                options.Headers["X-Agent-Key"] = config.Key;
            })
            .AddJsonProtocol(options => options.PayloadSerializerOptions.Converters.Add(new JsonStringEnumConverter()))
            .Build();
        hub.On<FamilyScanRequestDto, bool>(nameof(IAgentClient.StartFamilyScan), _ => false);
        await hub.StartAsync(ct);
        await hub.InvokeAsync<AgentSettingsDto>(nameof(IAgentHub.Hello),
            new AgentHelloDto("BIM-FARM-REAL", "2.5.0", 65536, [new InstalledRevitDto(2022, true)], [ModuleKinds.FamiliesRead]), ct);
        await using (var scope = server.Services.CreateAsyncScope()) await scope.ServiceProvider.GetRequiredService<FamilyReadJobs>().SweepAsync(ct);

        var root = Environment.GetEnvironmentVariable("TF_REAL_ROOT")!.TrimEnd('\\');
        int ok = 0, failed = 0, missing = 0;
        var watch = Stopwatch.StartNew();
        while (await hub.InvokeAsync<JobAssignmentDto?>(nameof(IAgentHub.RequestJob), new JobRequestDto(1, 2022, true), ct) is { } job)
        {
            var payload = JsonSerializer.Deserialize<FamilyReadPayload>(job.PayloadJson, Json)!;
            var relative = payload.Path[(root.Length + 1)..];
            JobCompletionDto completion;
            if (results.TryGetValue(relative, out var fields) && fields[1] == "OK")
            {
                var content = JsonSerializer.Deserialize<Alexpivo394.Revit.Families.Files.FamilyContent>(fields[4], Json)!;
                var png = Path.Combine(reads, "previews", payload.Hash + ".png");
                var result = new FamilyReadResult(content, File.Exists(png) ? Convert.ToBase64String(await File.ReadAllBytesAsync(png, ct)) : null);
                completion = new JobCompletionDto(job.JobId, JobOutcome.Succeeded, null, JsonSerializer.Serialize(result, Json), job.Attempt);
                ok++;
            }
            else if (fields is not null && fields[1] == "FAIL")
            {
                completion = new JobCompletionDto(job.JobId, JobOutcome.Failed, $"Revit не открыл файл: {fields[3]}", null, job.Attempt);
                failed++;
            }
            else
            {
                // Файл изменён правками шага 3 или не читался прогоном — как «файл изменился после обхода».
                completion = new JobCompletionDto(job.JobId, JobOutcome.Warning, "файл изменился после обхода", null, job.Attempt);
                missing++;
            }

            await hub.InvokeAsync(nameof(IAgentHub.CompleteJob), completion, ct);
        }

        Line($"- сдано заданий: прочитано {ok}, не открылось {failed}, без итога {missing} — за {watch.Elapsed.TotalSeconds:0.0} с");
        var withPreview = await DbAsync(server, db => db.Set<FamilyVersion>().AsNoTracking().CountAsync(v => v.HasPreview));
        Line($"- версий с картинкой: {withPreview}");
        var top = await DbAsync(server, db => db.Set<FamilyCard>().AsNoTracking().Where(c => c.Category != "").GroupBy(c => c.Category)
            .Select(g => new { g.Key, Count = g.Count() }).OrderByDescending(g => g.Count).Take(8).ToListAsync());
        Line($"- категории: {string.Join(", ", top.Select(c => $"{c.Key} {c.Count}"))}");
        var types = await DbAsync(server, db => db.Set<FamilyCard>().AsNoTracking().SumAsync(c => c.TypeCount));
        Line($"- типоразмеров в каталоге: {types}");
        await StatsAsync(server, admin);
    }

    private async Task SearchAsync(HttpClient admin)
    {
        foreach (var query in new[] { "клапан", "кран шаровой", "радиатор", "dn50", "вентустановка", "%", "ADSK", "обратный клапан" })
        {
            var watch = Stopwatch.StartNew();
            var page = await admin.GetFromJsonAsync<Page<FamilyHit>>($"/api/families/search?q={Uri.EscapeDataString(query)}&pageSize=5", Json);
            Line($"- «{query}»: найдено {page!.Total} за {watch.ElapsedMilliseconds} мс; первые: {string.Join("; ", page.Items.Select(h => h.Name))}");
        }
    }

    private static async Task<int> CountAsync<T>(ServerFactory server) where T : class => await DbAsync(server, db => db.Set<T>().CountAsync());

    private static async Task<T> DbAsync<T>(ServerFactory server, Func<ToadFlowDb, Task<T>> action)
    {
        await using var scope = server.Services.CreateAsyncScope();
        return await action(scope.ServiceProvider.GetRequiredService<ToadFlowDb>());
    }
}
