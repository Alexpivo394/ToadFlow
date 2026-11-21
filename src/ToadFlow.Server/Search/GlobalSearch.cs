using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using ToadFlow.Contracts;
using ToadFlow.Server.Auth;
using ToadFlow.Server.Data;
using ToadFlow.Server.Modules;

namespace ToadFlow.Server.Search;

/// <summary>Строка быстрого поиска.</summary>
/// <param name="Kind">Вид: <c>family</c>, <c>request</c>, <c>schedule</c>, <c>part</c>, <c>user</c>.</param>
/// <param name="Link">Путь веба.</param>
public sealed record SearchHit(string Kind, string Title, string? Subtitle, string Link);

/// <summary>Что модуль отдаёт в быстрый поиск. Регистрируется в <c>AddServices</c> модуля; права проверяет сам источник.</summary>
public interface ISearchSource
{
    Task<IReadOnlyList<SearchHit>> SearchAsync(IServiceProvider services, ClaimsPrincipal user, string q, CancellationToken ct);
}

/// <summary>Быстрый поиск (Ctrl+K): по <see cref="PerKind" /> строк каждого вида от ядра и включённых модулей.</summary>
public static class GlobalSearch
{
    public const int PerKind = 5;
    public const int MinLength = 2;

    public static void MapGlobalSearch(this RouteGroupBuilder members) => members.MapGet("/search", SearchAsync);

    public static void AddGlobalSearch(this IServiceCollection services) => services.AddSingleton<ISearchSource, CoreSearchSource>();

    private static async Task<List<SearchHit>> SearchAsync(string? q, HttpContext http, IEnumerable<ISearchSource> sources, ILoggerFactory loggers, CancellationToken ct)
    {
        var query = q?.Trim() ?? "";
        if (query.Length < MinLength && !(query.Length > 0 && query.All(char.IsDigit))) return []; // номер заявки — и с одной цифры
        var hits = new List<SearchHit>();
        foreach (var source in sources)
        {
            try
            {
                hits.AddRange(await source.SearchAsync(http.RequestServices, http.User, query, ct));
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                loggers.CreateLogger(typeof(GlobalSearch)).LogError(exception, "Быстрый поиск: источник {Source} не ответил", source.GetType().Name);
            }
        }

        return hits.GroupBy(h => h.Kind).SelectMany(g => g.Take(PerKind)).ToList();
    }

    /// <summary>Шаблон <c>ILIKE</c> для вхождения строки.</summary>
    public static string Like(string q) => "%" + q.Replace(@"\", @"\\").Replace("%", @"\%").Replace("_", @"\_") + "%";
}

/// <summary>Ядро: расписания включённых модулей — от наблюдателя; пользователи — администратору.</summary>
internal sealed class CoreSearchSource(ModuleSet modules) : ISearchSource
{
    public async Task<IReadOnlyList<SearchHit>> SearchAsync(IServiceProvider services, ClaimsPrincipal user, string q, CancellationToken ct)
    {
        if (user.IsInRole(nameof(UserRole.Designer))) return [];
        var db = services.GetRequiredService<ToadFlowDb>();
        var like = GlobalSearch.Like(q);
        var hits = new List<SearchHit>();
        var kinds = modules.EnabledKinds;
        var schedules = await db.Schedules.AsNoTracking().Where(s => kinds.Contains(s.ModuleKind) && EF.Functions.ILike(s.Name, like, @"\"))
            .OrderBy(s => s.Name).Take(GlobalSearch.PerKind).Select(s => new { s.Id, s.Name, s.ModuleKind }).ToListAsync(ct);
        foreach (var schedule in schedules)
        {
            var module = modules.Enabled.First(m => m.Kinds.Contains(schedule.ModuleKind));
            var history = module.HistoryPath;
            var link = history.EndsWith("/history") ? $"{history[..^"history".Length]}schedules/{schedule.Id}" : module.WebPath;
            hits.Add(new SearchHit("schedule", schedule.Name, $"Расписание · {module.Title}", link));
        }

        if (user.IsInRole(nameof(UserRole.Admin)))
            hits.AddRange(await db.Users.AsNoTracking()
                .Where(u => !u.IsPending && (EF.Functions.ILike(u.DisplayName, like, @"\") || EF.Functions.ILike(u.Login, like, @"\")))
                .OrderBy(u => u.DisplayName).Take(GlobalSearch.PerKind)
                .Select(u => new SearchHit("user", u.DisplayName, u.Login, "/admin/users?q=" + u.Login)).ToListAsync(ct));
        return hits;
    }
}
