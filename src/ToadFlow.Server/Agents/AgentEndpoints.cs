using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using ToadFlow.Contracts;
using ToadFlow.Server.Auth;
using ToadFlow.Server.Data;
using ToadFlow.Server.Data.Entities;
using ToadFlow.Server.Events;
using ToadFlow.Server.Settings;

namespace ToadFlow.Server.Agents;

public sealed record RegisterAgentRequest(string Code, string MachineName);

public sealed record RegisterAgentResponse(Guid AgentId, string Key);

public sealed record RegistrationCodeResponse(string Code, DateTimeOffset ExpiresAt);

public sealed record DrainRequest(bool Draining);

public sealed record AgentView(
    Guid Id,
    string MachineName,
    string? Ip,
    string AgentVersion,
    long TotalRamMb,
    AgentState State,
    bool IsDraining,
    DateTimeOffset? LastSeenAt,
    double CpuPercent,
    long RamUsedMb,
    IReadOnlyList<InstalledRevitDto> Revits,
    IReadOnlyList<VersionLimitDto> Limits,
    IReadOnlyList<InstanceView> Instances,
    bool IsOutdated);

/// <summary>Экземпляр Revit для интерфейса: состояние от агента плюс имя модели, которую он выгружает.</summary>
public sealed record InstanceView(
    int Slot,
    int Year,
    int? Pid,
    InstanceState State,
    Guid? JobId,
    string? Stage,
    DateTimeOffset? StageSince,
    string? ModelName);

public static class AgentEndpoints
{
    private static readonly TimeSpan CodeLifetime = TimeSpan.FromHours(24);
    private const string CodeAlphabet = "ABCDEFGHJKMNPQRSTUVWXYZ23456789";

    public static void MapAgents(this RouteGroupBuilder api)
    {
        api.MapPost("/agents/register", RegisterAsync).AllowAnonymous();

        var agents = api.MapGroup("/agents");
        agents.MapGet("/", ListAsync);

        var admin = agents.MapGroup("").RequireAuthorization(Policies.Admin);
        admin.MapPost("/registration-codes", CreateCodeAsync);
        admin.MapPut("/{id:guid}/limits", SetLimitsAsync);
        admin.MapPost("/{id:guid}/drain", DrainAsync);
        admin.MapPost("/{id:guid}/disable", DisableAsync);
        admin.MapDelete("/{id:guid}", DeleteAsync);
    }

    private static async Task<RegistrationCodeResponse> CreateCodeAsync(ToadFlowDb db, HttpContext http, TimeProvider time)
    {
        var now = time.GetUtcNow();
        var code = new RegistrationCode
        {
            Code = $"TF-{RandomNumberGenerator.GetString(CodeAlphabet, 4)}-{RandomNumberGenerator.GetString(CodeAlphabet, 4)}",
            CreatedByUserId = http.User.UserId(),
            CreatedAt = now,
            ExpiresAt = now + CodeLifetime
        };
        db.RegistrationCodes.Add(code);
        await db.SaveChangesAsync();
        return new RegistrationCodeResponse(code.Code, code.ExpiresAt);
    }

    private static async Task<IResult> RegisterAsync(RegisterAgentRequest request, ToadFlowDb db, TimeProvider time, IDomainEvents events)
    {
        var now = time.GetUtcNow();
        var codeText = (request.Code ?? "").Trim().ToUpperInvariant();
        var code = await db.RegistrationCodes.SingleOrDefaultAsync(c => c.Code == codeText);
        if (code is null || code.UsedAt is not null || code.ExpiresAt < now)
            return Results.BadRequest(new { error = "Код недействителен: он неверный, уже использован или истёк" });

        var key = Base64Url(RandomNumberGenerator.GetBytes(32));
        var agent = new Agent
        {
            Id = Guid.CreateVersion7(),
            MachineName = string.IsNullOrWhiteSpace(request.MachineName) ? "без имени" : request.MachineName.Trim(),
            KeyHash = AgentAuthHandler.Hash(key),
            RegisteredAt = now
        };
        db.Agents.Add(agent);
        code.UsedAt = now;
        code.UsedByAgentId = agent.Id;
        await db.SaveChangesAsync();

        events.Publish(new AgentChanged(AgentHub.AgentActor(agent), agent.Id, $"Подключён новый агент {agent.MachineName}"));
        return Results.Ok(new RegisterAgentResponse(agent.Id, key));
    }

    private static async Task<IEnumerable<AgentView>> ListAsync(ToadFlowDb db, AgentRegistry registry)
    {
        var agents = await db.Agents.AsNoTracking().OrderBy(a => a.MachineName).ToListAsync();
        var newest = agents.Select(a => ParseVersion(a.AgentVersion)).DefaultIfEmpty(new Version(0, 0)).Max()!;
        var instances = agents.ToDictionary(a => a.Id, a => registry.Instances(a.Id));
        var jobIds = instances.Values.SelectMany(list => list).Where(i => i.JobId != null).Select(i => i.JobId!.Value).ToList();
        var models = await db.Jobs.AsNoTracking().Where(j => jobIds.Contains(j.Id)).ToDictionaryAsync(j => j.Id, j => j.ModelName);

        return agents.Select(a => new AgentView(a.Id, a.MachineName, a.Ip, a.AgentVersion, a.TotalRamMb, a.State, a.IsDraining, a.LastSeenAt,
            a.CpuPercent, a.RamUsedMb, a.Revits, a.Limits,
            instances[a.Id].Select(i => new InstanceView(i.Slot, i.Year, i.Pid, i.State, i.JobId, i.Stage, i.StageSince,
                i.JobId is { } jobId ? models.GetValueOrDefault(jobId) : null)).ToList(),
            a.AgentVersion.Length > 0 && ParseVersion(a.AgentVersion) < newest));
    }

    private static async Task<IResult> SetLimitsAsync(Guid id, VersionLimitDto[] limits, ToadFlowDb db, AgentRegistry registry,
        ISettingsStore settings, HttpContext http, IDomainEvents events)
    {
        var agent = await db.Agents.FindAsync(id);
        if (agent is null) return Results.NotFound();
        if (ValidateLimits(limits, agent) is { } error) return Results.BadRequest(new { error });

        agent.Limits = agent.Revits.Select(r => new VersionLimitDto(r.Year, limits.FirstOrDefault(l => l.Year == r.Year)?.MaxInstances ?? 0)).ToList();
        await db.SaveChangesAsync();
        await PushSettingsAsync(agent, registry, settings);
        events.Publish(new AgentChanged(http.User.Actor(), id,
            $"Лимиты агента {agent.MachineName}: {string.Join(", ", agent.Limits.Select(l => $"R{l.Year}: {l.MaxInstances}"))}"));
        return Results.Ok(agent.Limits);
    }

    private static async Task<IResult> DrainAsync(Guid id, DrainRequest request, ToadFlowDb db, AgentRegistry registry, ISettingsStore settings,
        HttpContext http, IDomainEvents events)
    {
        var agent = await db.Agents.FindAsync(id);
        if (agent is null) return Results.NotFound();
        if (agent.State == AgentState.Disabled) return Results.Conflict(new { error = "Агент отключён" });

        agent.IsDraining = request.Draining;
        if (agent.State != AgentState.Offline) agent.State = request.Draining ? AgentState.Draining : AgentState.Online;
        await db.SaveChangesAsync();
        await PushSettingsAsync(agent, registry, settings);
        events.Publish(new AgentChanged(http.User.Actor(), id,
            request.Draining ? $"Агент {agent.MachineName} больше не берёт новые задания" : $"Агент {agent.MachineName} снова берёт задания"));
        return Results.NoContent();
    }

    private static async Task<IResult> DisableAsync(Guid id, ToadFlowDb db, AgentRegistry registry, HttpContext http, IDomainEvents events)
    {
        var agent = await db.Agents.FindAsync(id);
        if (agent is null) return Results.NotFound();

        agent.State = AgentState.Disabled;
        await db.SaveChangesAsync();
        registry.Abort(id);
        events.Publish(new AgentChanged(http.User.Actor(), id, $"Агент {agent.MachineName} отключён"));
        return Results.NoContent();
    }

    private static async Task<IResult> DeleteAsync(Guid id, ToadFlowDb db, AgentRegistry registry, HttpContext http, IDomainEvents events)
    {
        var agent = await db.Agents.FindAsync(id);
        if (agent is null) return Results.NotFound();
        if (agent.State is not (AgentState.Offline or AgentState.Disabled) || registry.IsConnected(id))
            return Results.Conflict(new { error = "Удалить можно только агента, который не в сети или отключён" });

        db.Agents.Remove(agent);
        await db.SaveChangesAsync();
        events.Publish(new AgentChanged(http.User.Actor(), id, $"Агент {agent.MachineName} удалён"));
        return Results.NoContent();
    }

    internal static async Task PushSettingsAsync(Agent agent, AgentRegistry registry, ISettingsStore settings)
    {
        if (registry.Client(agent.Id) is { } client) await client.ApplySettings(AgentSettingsFactory.Build(agent, await settings.GetAsync()));
    }

    private static string? ValidateLimits(IReadOnlyList<VersionLimitDto> limits, Agent agent)
    {
        if (limits.Any(l => l.MaxInstances < 0)) return "Число экземпляров не может быть отрицательным";
        if (limits.Any(l => agent.Revits.All(r => r.Year != l.Year))) return "На агенте нет Revit такой версии";
        if (limits.Sum(l => l.MaxInstances) > AgentSettingsFactory.MaxInstancesPerAgent)
            return $"Не больше {AgentSettingsFactory.MaxInstancesPerAgent} экземпляров Revit на агента";
        return null;
    }

    private static Version ParseVersion(string text) => Version.TryParse(text, out var version) ? version : new Version(0, 0);

    private static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
