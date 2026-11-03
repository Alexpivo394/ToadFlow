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
    return new EngineClient(http);
});
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<SnapshotCache>();
builder.Services.AddSingleton<EngineWorker>();
builder.Services.AddHostedService<WorkerLoop>();

await builder.Build().RunAsync();

/// <summary>
///     Главный цикл: прогон движка из очереди; очередь пуста или сервер недоступен — пауза.
/// </summary>
internal sealed class WorkerLoop(EngineWorker worker, WorkerOptions options, ILogger<WorkerLoop> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        if (string.IsNullOrEmpty(options.EngineKey)) logger.LogWarning("Не задан Clash:EngineKey — сервер не пустит работника");
        logger.LogInformation("Работник движка: сервер {Server}, кеш {Cache}", options.ServerUrl, options.ResolvedCacheRoot);

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
