using System.Collections.Concurrent;
using Microsoft.AspNetCore.SignalR;
using ToadFlow.Contracts;

namespace ToadFlow.Server.Agents;

/// <summary>Агенты на связи: их соединения и последнее состояние экземпляров Revit. Живёт только в памяти сервера.</summary>
public sealed class AgentRegistry(IHubContext<AgentHub, IAgentClient> hub)
{
    private readonly ConcurrentDictionary<Guid, Connection> _connections = new();

    public void Connected(Guid agentId, HubCallerContext context)
    {
        if (_connections.TryRemove(agentId, out var previous) && previous.Context.ConnectionId != context.ConnectionId)
            previous.Context.Abort(); // второе соединение того же агента вытесняет первое
        _connections[agentId] = new Connection(context, []);
    }

    /// <returns>true — отключилось текущее соединение агента (а не уже вытесненное).</returns>
    public bool Disconnected(Guid agentId, string connectionId) =>
        _connections.TryGetValue(agentId, out var current) && current.Context.ConnectionId == connectionId &&
        _connections.TryRemove(agentId, out _);

    public void Beat(Guid agentId, InstanceStatusDto[] instances)
    {
        if (_connections.TryGetValue(agentId, out var current)) _connections[agentId] = current with { Instances = instances };
    }

    public InstanceStatusDto[] Instances(Guid agentId) => _connections.TryGetValue(agentId, out var current) ? current.Instances : [];

    public bool IsConnected(Guid agentId) => _connections.ContainsKey(agentId);

    public IReadOnlyList<Guid> Online() => _connections.Keys.ToList();

    public IAgentClient? Client(Guid agentId) =>
        _connections.TryGetValue(agentId, out var current) ? hub.Clients.Client(current.Context.ConnectionId) : null;

    /// <summary>Разорвать соединение (агента отключили).</summary>
    public void Abort(Guid agentId)
    {
        if (_connections.TryRemove(agentId, out var current)) current.Context.Abort();
    }

    private sealed record Connection(HubCallerContext Context, InstanceStatusDto[] Instances);
}
