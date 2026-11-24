using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using ToadFlow.Server.Auth;
using ToadFlow.Server.Data;
using ToadFlow.Server.Modules;
using ToadFlow.Server.Overview;
using ToadFlow.Server.Settings;

namespace ToadFlow.Server.Usage.Service;

/// <summary>
///     Вызовы API плагинами: запрос, вошедший личным токеном (<c>X-ToadFlow-Token</c>), с путём <c>/api/{раздел}/…</c>, где раздел —
///     включённый модуль, кроме самой аналитики. Счёт копится в памяти по (человек, день, раздел) и раз в минуту и при остановке
///     сервера прибавляется к <c>usage_plugin_days</c>. Служба есть только у включённого модуля.
/// </summary>
public sealed class PluginCallCounter(IServiceScopeFactory scopes, ModuleSet modules, TimeProvider time)
{
    private ConcurrentDictionary<(Guid User, DateOnly Day, string Area), int> _counts = new();
    private TimeZoneInfo? _zone;

    /// <summary>Учитывает запрос после того, как он обработан (тогда известен вошедший).</summary>
    public void Observe(HttpContext http)
    {
        var user = http.User;
        if (user.FindFirst(ApiTokenAuthHandler.TokenClaim) is null) return;
        var area = AreaOf(http.Request.Path);
        if (area is null || area == "usage" || !modules.IsEnabled(area)) return;
        if (!Guid.TryParse(user.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value, out var userId)) return;
        var day = OverviewDays.DayOf(time.GetUtcNow(), _zone ?? TimeZoneInfo.Utc);
        _counts.AddOrUpdate((userId, day, area), 1, (_, count) => count + 1);
    }

    /// <summary>Раздел API: первый сегмент после <c>/api/</c>.</summary>
    internal static string? AreaOf(PathString path)
    {
        var value = path.Value;
        if (value is null || !value.StartsWith("/api/", StringComparison.OrdinalIgnoreCase)) return null;
        var rest = value["/api/".Length..];
        var slash = rest.IndexOf('/');
        var area = (slash < 0 ? rest : rest[..slash]).ToLowerInvariant();
        return area.Length == 0 ? null : area;
    }

    /// <summary>Прибавляет накопленное к таблице и обнуляет память; заодно обновляет часовой пояс для дней.</summary>
    public async Task FlushAsync(CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        _zone = (await scope.ServiceProvider.GetRequiredService<ISettingsStore>().GetAsync(ct)).TimeZone;
        var counts = Interlocked.Exchange(ref _counts, new ConcurrentDictionary<(Guid, DateOnly, string), int>());
        if (counts.IsEmpty) return;

        var db = scope.ServiceProvider.GetRequiredService<ToadFlowDb>();
        var connection = (NpgsqlConnection)db.Database.GetDbConnection();
        await db.Database.OpenConnectionAsync(ct);
        try
        {
            await using var batch = new NpgsqlBatch(connection);
            foreach (var ((user, day, area), count) in counts)
            {
                var command = new NpgsqlBatchCommand("""
                    INSERT INTO usage_plugin_days (user_id, day, area, count) VALUES ($1, $2, $3, $4)
                    ON CONFLICT (user_id, day, area) DO UPDATE SET count = usage_plugin_days.count + excluded.count
                    """);
                command.Parameters.Add(new NpgsqlParameter { Value = user });
                command.Parameters.Add(new NpgsqlParameter { Value = day });
                command.Parameters.Add(new NpgsqlParameter { Value = area });
                command.Parameters.Add(new NpgsqlParameter { Value = count });
                batch.BatchCommands.Add(command);
            }

            await batch.ExecuteNonQueryAsync(ct);
        }
        finally
        {
            await db.Database.CloseConnectionAsync();
        }
    }
}

/// <summary>Сброс счётчика вызовов плагинов раз в минуту и при остановке сервера.</summary>
internal sealed class PluginCallFlusher(PluginCallCounter counter, ILogger<PluginCallFlusher> logger) : BackgroundService
{
    private static readonly TimeSpan Period = TimeSpan.FromMinutes(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Сразу при старте: счётчик узнаёт часовой пояс «Общих», по которому считает дни.
        await FlushAsync(stoppingToken);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(Period, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            await FlushAsync(stoppingToken);
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await base.StopAsync(cancellationToken);
        await FlushAsync(cancellationToken);
    }

    private async Task FlushAsync(CancellationToken ct)
    {
        try
        {
            await counter.FlushAsync(ct);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(exception, "Аналитика: не сохранены вызовы плагинов");
        }
    }
}

/// <summary>Встраивает учёт вызовов плагинов в начало конвейера сервера: после обработки запроса вошедший уже известен.</summary>
internal sealed class PluginCallStartupFilter : IStartupFilter
{
    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
    {
        var counter = app.ApplicationServices.GetRequiredService<PluginCallCounter>();
        app.Use(async (http, nextMiddleware) =>
        {
            await nextMiddleware(http);
            try
            {
                counter.Observe(http);
            }
            catch (Exception)
            {
                // Учёт не должен ломать ответ.
            }
        });
        next(app);
    };
}
