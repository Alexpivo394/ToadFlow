using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using ToadFlow.Contracts;
using ToadFlow.Server.Data;
using ToadFlow.Server.Data.Entities;
using ToadFlow.Server.Events;
using ToadFlow.Server.Settings;

namespace ToadFlow.Server.Agents;

/// <summary>Хаб агентов: агент сам держит исходящее соединение, по нему идут и его вызовы, и команды сервера.</summary>
[Authorize(AuthenticationSchemes = AgentAuthHandler.SchemeName)]
public sealed class AgentHub(ToadFlowDb db, AgentRegistry registry, ISettingsStore settings, IDomainEvents events, TimeProvider time,
    IAgentJobs jobs) : Hub<IAgentClient>, IAgentHub
{
    /// <summary>На сколько продлевается аренда задания каждым heartbeat.</summary>
    public static readonly TimeSpan Lease = TimeSpan.FromMinutes(2);

    private Guid AgentId => Guid.Parse(Context.User!.FindFirstValue(ClaimTypes.NameIdentifier)!);

    public override Task OnConnectedAsync()
    {
        registry.Connected(AgentId, Context);
        return Task.CompletedTask;
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        if (!registry.Disconnected(AgentId, Context.ConnectionId)) return;

        var agent = await db.Agents.FindAsync(AgentId);
        if (agent is null || agent.State is AgentState.Offline or AgentState.Disabled) return;

        agent.State = AgentState.Offline;
        await db.SaveChangesAsync();
        events.Publish(new AgentWentOffline(AgentActor(agent), agent.Id, agent.MachineName));
    }

    public async Task<AgentSettingsDto> Hello(AgentHelloDto hello)
    {
        var agent = await db.Agents.SingleAsync(a => a.Id == AgentId);
        agent.MachineName = hello.MachineName;
        agent.AgentVersion = hello.AgentVersion;
        agent.TotalRamMb = hello.TotalRamMb;
        agent.Ip = Context.GetHttpContext()?.Connection.RemoteIpAddress?.ToString();
        agent.Revits = hello.Revits.OrderBy(r => r.Year).ToList();
        agent.Limits = AgentSettingsFactory.ReconcileLimits(agent.Limits, agent.Revits);
        agent.State = agent.IsDraining ? AgentState.Draining : AgentState.Online;
        agent.LastSeenAt = time.GetUtcNow();
        await db.SaveChangesAsync();

        events.Publish(new AgentWentOnline(AgentActor(agent), agent.Id, agent.MachineName));
        return AgentSettingsFactory.Build(agent, await settings.GetAsync());
    }

    public async Task Heartbeat(AgentHeartbeatDto beat)
    {
        var now = time.GetUtcNow();
        registry.Beat(AgentId, beat.Instances);
        await db.Agents.Where(a => a.Id == AgentId).ExecuteUpdateAsync(a => a
            .SetProperty(x => x.CpuPercent, beat.CpuPercent)
            .SetProperty(x => x.RamUsedMb, beat.RamUsedMb)
            .SetProperty(x => x.LastSeenAt, now));

        var held = beat.Instances.Where(i => i.JobId is not null).Select(i => i.JobId!.Value).ToList();
        if (held.Count > 0)
        {
            await db.Jobs.Where(j => held.Contains(j.Id) && j.AgentId == AgentId && j.Status == JobStatus.Running)
                .ExecuteUpdateAsync(j => j.SetProperty(x => x.LeaseUntil, now + Lease));
        }

        events.Publish(new AgentStatusReceived(Actor.System, AgentId));
    }

    public Task<JobAssignmentDto?> RequestJob(JobRequestDto request) => jobs.ClaimAsync(AgentId, request);

    public Task ReportProgress(JobProgressDto progress) => jobs.AppendLogAsync(AgentId, progress);

    public Task CompleteJob(JobCompletionDto completion) => jobs.CompleteAsync(AgentId, completion);

    public async Task ReportInstanceKilled(int slot, string reason)
    {
        var agent = await db.Agents.AsNoTracking().SingleAsync(a => a.Id == AgentId);
        events.Publish(new InstanceKilled(AgentActor(agent), agent.Id, agent.MachineName, slot, reason));
    }

    internal static Actor AgentActor(Agent agent) => new(ActorKind.Agent, agent.MachineName);
}

/// <summary>Работа агента с очередью заданий.</summary>
public interface IAgentJobs
{
    Task<JobAssignmentDto?> ClaimAsync(Guid agentId, JobRequestDto request);

    Task AppendLogAsync(Guid agentId, JobProgressDto progress);

    Task CompleteAsync(Guid agentId, JobCompletionDto completion);
}

public static class AgentSettingsFactory
{
    public const int DefaultInstancesPerAgent = 3;

    public static AgentSettingsDto Build(Agent agent, GeneralSettings general) => new(
        agent.Limits.ToArray(), general.SilenceTimeoutMinutes, general.RecycleAfterModels, general.RecycleMemoryMb, agent.IsDraining);

    /// <summary>
    ///     Лимиты под установленные версии: для известных версий остаются прежние, новые получают по одному экземпляру,
    ///     пока не набралось <see cref="DefaultInstancesPerAgent" />, остальные — ноль. Удалённые версии пропадают.
    /// </summary>
    public static List<VersionLimitDto> ReconcileLimits(IReadOnlyList<VersionLimitDto> current, IReadOnlyList<InstalledRevitDto> revits)
    {
        var kept = current.Where(l => revits.Any(r => r.Year == l.Year)).ToDictionary(l => l.Year, l => l.MaxInstances);
        var total = kept.Values.Sum();
        foreach (var revit in revits.OrderBy(r => r.Year).Where(r => !kept.ContainsKey(r.Year)))
        {
            var slots = total < DefaultInstancesPerAgent ? 1 : 0;
            kept[revit.Year] = slots;
            total += slots;
        }

        return kept.OrderBy(pair => pair.Key).Select(pair => new VersionLimitDto(pair.Key, pair.Value)).ToList();
    }
}
