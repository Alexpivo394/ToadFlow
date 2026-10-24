using System;
using System.Threading.Tasks;

namespace ToadFlow.Contracts;

/// <summary>Методы сервера, которые вызывает агент по хабу <c>/hubs/agent</c>.</summary>
public interface IAgentHub
{
    Task<AgentSettingsDto> Hello(AgentHelloDto hello);

    Task Heartbeat(AgentHeartbeatDto beat);

    /// <summary>Задание для свободного экземпляра Revit этого года; null — подходящих нет.</summary>
    Task<JobAssignmentDto?> RequestJob(JobRequestDto request);

    Task ReportProgress(JobProgressDto progress);

    Task CompleteJob(JobCompletionDto completion);

    /// <summary>Агент сам убил экземпляр Revit (таймаут, молчание, не запустился) — для «Событий системы».</summary>
    Task ReportInstanceKilled(int slot, string reason);
}

/// <summary>Методы агента, которые вызывает сервер.</summary>
public interface IAgentClient
{
    Task<SourceEntryDto[]> ListChildren(SourceDto source, string folderPath);

    Task<ExpandedModelDto[]> ExpandSelection(SourceDto source, SelectionDto selection);

    Task KillInstance(int slot);

    Task CancelJob(Guid jobId);

    Task ApplySettings(AgentSettingsDto settings);
}

public sealed record InstalledRevitDto(int Year, bool HasNwcExporter);

public sealed record AgentHelloDto(string MachineName, string AgentVersion, long TotalRamMb, InstalledRevitDto[] Revits);

public sealed record VersionLimitDto(int Year, int MaxInstances);

public sealed record AgentSettingsDto(
    VersionLimitDto[] Limits,
    int SilenceTimeoutMinutes,
    int RecycleAfterModels,
    int RecycleMemoryMb,
    bool Draining);

public sealed record InstanceStatusDto(
    int Slot,
    int Year,
    int? Pid,
    InstanceState State,
    Guid? JobId,
    string? Stage,
    DateTimeOffset? StageSince);

public sealed record AgentHeartbeatDto(double CpuPercent, long RamUsedMb, InstanceStatusDto[] Instances);

public sealed record JobRequestDto(int Slot, int Year, bool HasNwcExporter);

public sealed record JobAssignmentDto(
    Guid JobId,
    string ModuleKind,
    string ModelName,
    string PayloadJson,
    int TimeoutMinutes,
    int Attempt);

public sealed record JobLogDto(DateTimeOffset At, string Stage, string Text);

public sealed record JobProgressDto(Guid JobId, int Slot, JobLogDto[] Lines);

public sealed record JobCompletionDto(Guid JobId, JobOutcome Outcome, string? Note, string? ResultJson);

public static class ModuleKinds
{
    public const string Export = "export";

    /// <summary>Проверка заполнения параметров (LOI) по профилю ParamChecker.</summary>
    public const string Loi = "loi";

    /// <summary>Снять в Revit категории и имена параметров для редактора правил LOI. Задание без модели и без расписания.</summary>
    public const string LoiCatalog = "loi-catalog";
}
