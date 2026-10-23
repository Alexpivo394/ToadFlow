using Microsoft.EntityFrameworkCore;
using ToadFlow.Contracts;
using ToadFlow.Server.Auth;
using ToadFlow.Server.Data;
using ToadFlow.Server.Events;

namespace ToadFlow.Server.Agents;

/// <summary>«Убить» экземпляр Revit и дерево источника — оба идут через агента на связи.</summary>
public static class InstanceEndpoints
{
    public static readonly TimeSpan TreeTimeout = TimeSpan.FromSeconds(30);

    public static void MapInstances(this RouteGroupBuilder api)
    {
        var write = api.MapGroup("").RequireAuthorization(Policies.Operator);

        write.MapPost("/agents/{id:guid}/instances/{slot:int}/kill", async (Guid id, int slot, ToadFlowDb db, AgentRegistry registry,
            HttpContext http, IDomainEvents events) =>
        {
            var agent = await db.Agents.AsNoTracking().SingleOrDefaultAsync(a => a.Id == id);
            if (agent is null) return Results.NotFound();
            if (registry.Client(id) is not { } client) return Results.Conflict(new { error = "Агент не на связи" });

            await client.KillInstance(slot);
            events.Publish(new InstanceKilled(http.User.Actor(), id, agent.MachineName, slot, null));
            return Results.NoContent();
        });

        write.MapGet("/sources/{id:guid}/tree", async (Guid id, string? path, ToadFlowDb db, AgentRegistry registry, ILoggerFactory logs) =>
        {
            var source = await db.Sources.AsNoTracking().SingleOrDefaultAsync(s => s.Id == id);
            if (source is null) return Results.NotFound();

            var online = registry.Online();
            var agents = await db.Agents.AsNoTracking().Where(a => online.Contains(a.Id) && a.State != AgentState.Disabled).Select(a => a.Id).ToListAsync();
            foreach (var agentId in agents)
            {
                if (registry.Client(agentId) is not { } client) continue;
                try
                {
                    return Results.Ok(await client.ListChildren(source.ToDto(), path ?? "").WaitAsync(TreeTimeout));
                }
                catch (Exception exception)
                {
                    logs.CreateLogger("SourceTree").LogWarning(exception, "Агент {Agent} не смог прочитать источник {Source}", agentId, source.Name);
                }
            }

            return Results.Json(new { error = "Нет агентов в сети, которые видят этот источник" }, statusCode: StatusCodes.Status503ServiceUnavailable);
        });
    }
}
