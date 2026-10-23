using ToadFlow.Contracts;

namespace ToadFlow.Server.Data.Entities;

public sealed class Agent
{
    public Guid Id { get; set; }
    public string MachineName { get; set; } = "";
    public string? Ip { get; set; }
    public string AgentVersion { get; set; } = "";
    public long TotalRamMb { get; set; }

    /// <summary>SHA-256 ключа агента в hex; сам ключ знает только агент.</summary>
    public string KeyHash { get; set; } = "";

    public AgentState State { get; set; } = AgentState.Offline;

    /// <summary>«Не брать новые»: доделывает текущее, новых заданий не получает.</summary>
    public bool IsDraining { get; set; }

    public DateTimeOffset? LastSeenAt { get; set; }
    public double CpuPercent { get; set; }
    public long RamUsedMb { get; set; }
    public DateTimeOffset RegisteredAt { get; set; }
    public List<InstalledRevitDto> Revits { get; set; } = [];
    public List<VersionLimitDto> Limits { get; set; } = [];
}

public sealed class RegistrationCode
{
    public string Code { get; set; } = "";
    public Guid CreatedByUserId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset? UsedAt { get; set; }
    public Guid? UsedByAgentId { get; set; }
}
