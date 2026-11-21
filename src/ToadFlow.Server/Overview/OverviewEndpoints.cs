using System.Security.Claims;
using ToadFlow.Contracts;
using ToadFlow.Server.Modules;

namespace ToadFlow.Server.Overview;

/// <summary>
///     <c>GET /api/overview</c> — «Обзор»: что требует внимания, плитка на включённый модуль, сведения о сервере.
///     <c>GET /api/server</c> — только сведения о сервере (страница «Сервер»).
/// </summary>
public static class OverviewEndpoints
{
    public static void MapOverview(this RouteGroupBuilder api)
    {
        api.MapGet("/overview", OverviewAsync);
        api.MapGet("/server", (ServerStatsReader stats, CancellationToken ct) => stats.ReadAsync(ct));
    }

    private static async Task<OverviewView> OverviewAsync(CoreAttention core, ServerStatsReader stats, OverviewPulseReader pulse, ModuleSet modules,
        IEnumerable<IOverviewSource> sources, IServiceProvider services, HttpContext http, ILoggerFactory loggers, CancellationToken ct)
    {
        var logger = loggers.CreateLogger(typeof(OverviewEndpoints));
        var enabled = sources.Where(s => modules.IsEnabled(s.Module))
            .OrderBy(s => modules.Enabled.ToList().FindIndex(m => m.Name == s.Module)).ToList();

        var attention = new List<AttentionItem>(await core.ListAsync(ct));
        var tiles = new List<OverviewTile>();
        foreach (var source in enabled)
        {
            var module = modules.Enabled.First(m => m.Name == source.Module);
            try
            {
                if (await source.TileAsync(services, ct) is { } tile) tiles.Add(tile);
            }
            catch (Exception exception) when (exception is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                logger.LogError(exception, "Модуль {Module} не отдал плитку «Обзора»", source.Module);
                tiles.Add(new OverviewTile(source.Module, "—", "нет данных", null, [], module.WebPath));
            }

            try
            {
                attention.AddRange(await source.AttentionAsync(services, ct));
            }
            catch (Exception exception) when (exception is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                logger.LogError(exception, "Модуль {Module} не отдал строки «Требует внимания»", source.Module);
            }
        }

        var rank = Rank(http.User);
        return new OverviewView(attention.Where(a => rank >= Rank(a.MinRole)).ToList(), tiles, await stats.ReadAsync(ct),
            await pulse.ReadAsync(ct));
    }

    /// <summary>Старшинство для строк внимания: Наблюдатель &lt; Координатор &lt; Администратор; проектировщик «Обзор» не видит.</summary>
    private static int Rank(UserRole role) => role switch
    {
        UserRole.Admin => 2,
        UserRole.Operator => 1,
        UserRole.Viewer => 0,
        _ => -1
    };

    private static int Rank(ClaimsPrincipal user) =>
        user.IsInRole(nameof(UserRole.Admin)) ? 2 : user.IsInRole(nameof(UserRole.Operator)) ? 1 : user.IsInRole(nameof(UserRole.Viewer)) ? 0 : -1;
}
