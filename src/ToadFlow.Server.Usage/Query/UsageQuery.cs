using Microsoft.EntityFrameworkCore;
using ToadFlow.Server.Data;
using ToadFlow.Server.Settings;
using ToadFlow.Server.Usage.Data;

namespace ToadFlow.Server.Usage.Query;

/// <summary>Общее для маршрутов чтения: фильтр из запроса и имена людей и машин для ответов.</summary>
internal sealed class UsageQuery(ToadFlowDb db, ISettingsStore settings, TimeProvider time)
{
    public ToadFlowDb Db => db;

    public DateTimeOffset Now => time.GetUtcNow();

    public async Task<TimeZoneInfo> ZoneAsync(CancellationToken ct) => (await settings.GetAsync(ct)).TimeZone;

    public async Task<UsageFilter> FilterAsync(HttpRequest request, CancellationToken ct) =>
        UsageFilter.Parse(request, await ZoneAsync(ct), time.GetUtcNow());

    /// <summary>Имена всех учёток (включая отключённые: их данные остаются).</summary>
    public async Task<Dictionary<Guid, string>> PeopleAsync(CancellationToken ct) =>
        await db.Users.AsNoTracking().ToDictionaryAsync(u => u.Id, u => u.DisplayName, ct);

    public async Task<Dictionary<string, string>> MachinesAsync(CancellationToken ct) =>
        await db.Set<UsageMachine>().AsNoTracking().ToDictionaryAsync(m => m.Key, m => m.Name, ct);

    public static UsagePerson Person(Dictionary<Guid, string> people, Guid id) => new(id, people.GetValueOrDefault(id, "Удалённая учётка"));

    public static UsageMachineRef Machine(Dictionary<string, string> machines, string key) => new(key, machines.GetValueOrDefault(key, key));
}

internal static class UsageBadRequest
{
    /// <summary>Неверный параметр запроса (<see cref="BadHttpRequestException" /> из разбора фильтра) — 400 с текстом, а не 500.</summary>
    public static RouteGroupBuilder WithBadRequests(this RouteGroupBuilder group)
    {
        group.AddEndpointFilter(async (context, next) =>
        {
            try
            {
                return await next(context);
            }
            catch (BadHttpRequestException exception)
            {
                return Results.BadRequest(new { error = exception.Message });
            }
        });
        return group;
    }
}
