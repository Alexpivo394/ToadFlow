using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ToadFlow.Clash.Worker;
using ToadFlow.Contracts.Clash;

var builder = Host.CreateApplicationBuilder(args);
builder.Services.Configure<WorkerOptions>(builder.Configuration.GetSection("Clash"));
builder.Services.AddSingleton(provider => provider.GetRequiredService<IOptions<WorkerOptions>>().Value);
builder.Services.AddSingleton(provider =>
{
    var options = provider.GetRequiredService<WorkerOptions>();
    var http = new HttpClient { BaseAddress = new Uri(options.ServerUrl.TrimEnd('/') + "/"), Timeout = TimeSpan.FromMinutes(10) };
    http.DefaultRequestHeaders.Add(EngineApi.KeyHeader, options.EngineKey);
    return http;
});
builder.Services.AddSingleton(provider => new EngineClient(provider.GetRequiredService<HttpClient>()));
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<SnapshotCache>();
builder.Services.AddSingleton<EngineWorker>();
builder.Services.AddHostedService<WorkerLoop>();

await builder.Build().RunAsync();
// Штатная остановка (сигнал при перезагрузке хоста) — не 0: иначе Docker с restart: on-failure не поднимет движок после
// перезапуска. 0 — только когда модуль выключен: тогда контейнер и не должен подниматься.
return WorkerLoop.ModuleDisabled ? 0 : WorkerLoop.StoppedExitCode;

/// <summary>
///     Главный цикл: прогон движка из очереди; очередь пуста или сервер недоступен — пауза. Сначала спрашивает сервер, включён ли
///     модуль коллизий: выключен — работник завершается с кодом 0, и контейнер с <c>restart: on-failure</c> больше не поднимается.
/// </summary>
internal sealed class WorkerLoop(EngineWorker worker, WorkerOptions options, HttpClient http, IHostApplicationLifetime lifetime, ILogger<WorkerLoop> logger)
    : BackgroundService
{
    /// <summary>Код выхода при остановке сигналом: 128 + SIGTERM, как у любого процесса, остановленного так.</summary>
    public const int StoppedExitCode = 143;

    /// <summary>Сервер ответил, что модуль коллизий выключен.</summary>
    public static bool ModuleDisabled { get; private set; }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        if (string.IsNullOrEmpty(options.EngineKey)) logger.LogWarning("Не задан Clash:EngineKey — сервер не пустит работника");
        logger.LogInformation("Работник движка: сервер {Server}, кеш {Cache}", options.ServerUrl, options.ResolvedCacheRoot);

        bool? enabled;
        while ((enabled = await ModuleGate.IsClashEnabledAsync(http, ct)) is null)
        {
            logger.LogWarning("Сервер недоступен — повтор через {Delay} с", options.PollInterval.TotalSeconds);
            await Task.Delay(options.PollInterval, ct);
        }

        if (enabled == false)
        {
            logger.LogInformation("Модуль коллизий выключен на сервере — работник завершается");
            ModuleDisabled = true;
            lifetime.StopApplication();
            return;
        }

        while (!ct.IsCancellationRequested)
        {
            try
            {
                if (await worker.RunOnceAsync(ct) is null) await Task.Delay(options.PollInterval, ct);
            }
            catch (Exception exception) when (exception is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                logger.LogWarning("Сервер недоступен ({Error}) — повтор через {Delay} с", exception.Message, options.PollInterval.TotalSeconds);
                await Task.Delay(options.PollInterval, ct);
            }
        }
    }
}
