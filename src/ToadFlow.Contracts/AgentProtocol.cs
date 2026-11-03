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

    /// <summary>
    ///     Задания, которые сервер считает выполняющимися у этого агента, с номерами попыток. Агент зовёт сразу после
    ///     (пере)подключения: чего сервер не назвал, того у агента уже нет, а названное можно доделывать и сдавать.
    /// </summary>
    Task<ActiveJobDto[]> GetActiveJobs();

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

/// <param name="Modules">Виды заданий, которые агент берёт (<see cref="ModuleKinds" />); null — агент до 2.0, сервер решает по версии.</param>
public sealed record AgentHelloDto(string MachineName, string AgentVersion, long TotalRamMb, InstalledRevitDto[] Revits, IReadOnlyList<string>? Modules = null);

public sealed record VersionLimitDto(int Year, int MaxInstances);

/// <param name="RamReserveMb">Сколько физической памяти оставлять Windows и другим программам: на её счёт Revit не запускается.</param>
/// <param name="RamPerJobMb">
///     Сколько памяти закладывать на задание, пока агент не видел, сколько на деле занимают модели этой машины.
///     Значения по умолчанию — для сервера до 1.6, который их не присылает.
/// </param>
public sealed record AgentSettingsDto(
    VersionLimitDto[] Limits,
    int SilenceTimeoutMinutes,
    int RecycleAfterModels,
    int RecycleMemoryMb,
    bool Draining,
    int RamReserveMb = AgentSettingsDto.DefaultRamReserveMb,
    int RamPerJobMb = AgentSettingsDto.DefaultRamPerJobMb)
{
    public const int DefaultRamReserveMb = 4096;
    public const int DefaultRamPerJobMb = 4096;
}

public sealed record InstanceStatusDto(
    int Slot,
    int Year,
    int? Pid,
    InstanceState State,
    Guid? JobId,
    string? Stage,
    DateTimeOffset? StageSince);

/// <param name="UploadingJobs">Попытки, которые агент сейчас загружает (Revit уже отпущен): сервер продлевает их аренду. null — агент до 2.0.</param>
public sealed record AgentHeartbeatDto(double CpuPercent, long RamUsedMb, InstanceStatusDto[] Instances, IReadOnlyList<ActiveJobDto>? UploadingJobs = null);

public sealed record JobRequestDto(int Slot, int Year, bool HasNwcExporter);

/// <param name="MaxAttempts">Сколько всего попыток у задания; 1 — сервер до 1.7.1, повтора не будет.</param>
public sealed record JobAssignmentDto(
    Guid JobId,
    string ModuleKind,
    string ModelName,
    string PayloadJson,
    int TimeoutMinutes,
    int Attempt,
    int MaxAttempts = 1);

public sealed record JobLogDto(DateTimeOffset At, string Stage, string Text);

/// <param name="Attempt">Номер попытки из выдачи; null — агент до 2.0, сервер ведёт себя как раньше.</param>
public sealed record JobProgressDto(Guid JobId, int Slot, JobLogDto[] Lines, int? Attempt = null);

/// <param name="Attempt">Номер попытки из выдачи; null — агент до 2.0, сервер ведёт себя как раньше.</param>
public sealed record JobCompletionDto(Guid JobId, JobOutcome Outcome, string? Note, string? ResultJson, int? Attempt = null);

/// <summary>Задание, которое сервер числит за агентом, и текущая попытка.</summary>
public sealed record ActiveJobDto(Guid JobId, int Attempt);

public static class ModuleKinds
{
    public const string Export = "export";

    /// <summary>Проверка заполнения параметров (LOI) по профилю ParamChecker.</summary>
    public const string Loi = "loi";

    /// <summary>Снять в Revit категории и имена параметров для редактора правил LOI. Задание без модели и без расписания.</summary>
    public const string LoiCatalog = "loi-catalog";

    /// <summary>Выгрузить геометрию и параметры модели для поиска коллизий и загрузить слепок на сервер.</summary>
    public const string ClashExtract = "clash-extract";
}
