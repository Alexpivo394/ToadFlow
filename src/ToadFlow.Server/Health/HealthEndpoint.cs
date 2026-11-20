using Microsoft.EntityFrameworkCore;
using ToadFlow.Server.Data;
using ToadFlow.Server.Modules;

namespace ToadFlow.Server.Health;

/// <summary>Дополнительное поле ответа <c>/api/health</c> от модуля (например, свободное место тома коллизий).</summary>
public interface IHealthProbe
{
    /// <summary>Имя поля в ответе.</summary>
    string Name { get; }

    /// <summary>Значение поля; ошибка пробы не роняет проверку — поле будет null.</summary>
    object? Probe();
}

/// <summary>
///     <c>GET /api/health</c> — для healthcheck контейнера и мониторинга: <c>{status, db, …пробы модулей}</c>. База недоступна —
///     503: сервер без базы не работает, контейнер должен считаться больным.
/// </summary>
public static class HealthEndpoint
{
    private static readonly TimeSpan DbTimeout = TimeSpan.FromSeconds(3);

    public static void MapHealth(this RouteGroupBuilder group) => group.MapGet("/health", CheckAsync).AllowAnonymous();

    private static async Task<IResult> CheckAsync(ToadFlowDb db, IEnumerable<IHealthProbe> probes, ModuleSet modules, CancellationToken ct)
    {
        bool dbUp;
        using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            timeout.CancelAfter(DbTimeout);
            try
            {
                dbUp = await db.Database.CanConnectAsync(timeout.Token);
            }
            catch (Exception exception) when (exception is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                dbUp = false;
            }
        }

        var body = new Dictionary<string, object?>
        {
            ["status"] = dbUp ? "ok" : "unhealthy",
            ["db"] = dbUp ? "ok" : "unavailable",
            // По нему рабочие процессы модулей (движок коллизий) узнают, включён ли их модуль.
            ["modules"] = modules.Enabled.Select(m => m.Name).ToArray()
        };
        foreach (var probe in probes)
        {
            try
            {
                body[probe.Name] = probe.Probe();
            }
            catch (Exception)
            {
                body[probe.Name] = null;
            }
        }

        return Results.Json(body, statusCode: dbUp ? StatusCodes.Status200OK : StatusCodes.Status503ServiceUnavailable);
    }
}
