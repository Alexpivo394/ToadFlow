using ToadFlow.Contracts;

namespace ToadFlow.Server.Data.Entities;

/// <summary>Один прогон расписания или ручной запуск.</summary>
public sealed class Run
{
    public Guid Id { get; set; }
    public Guid? ScheduleId { get; set; }

    /// <summary>Имя расписания на момент запуска — остаётся в истории, даже если расписание удалят.</summary>
    public string Name { get; set; } = "";

    /// <summary>Кто запустил вручную; null — по расписанию.</summary>
    public Guid? TriggeredByUserId { get; set; }

    public RunState State { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? FinishedAt { get; set; }
    public string? Warning { get; set; }

    /// <summary>Запуск отменили: когда все задания закончатся, он станет «отменён», а не «завершён».</summary>
    public bool CancelRequested { get; set; }
}

/// <summary>Одна модель в одном запуске.</summary>
public sealed class Job
{
    public Guid Id { get; set; }
    public Guid RunId { get; set; }
    public string ModuleKind { get; set; } = "";
    public string ModelName { get; set; } = "";
    public string ModelPath { get; set; } = "";
    public int? RevitYear { get; set; }
    public bool RequiresNwc { get; set; }
    public string PayloadJson { get; set; } = "{}";
    public JobStatus Status { get; set; }
    public int Attempt { get; set; }
    public int MaxAttempts { get; set; } = 1;
    public int Priority { get; set; }
    public int TimeoutMinutes { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? LeaseUntil { get; set; }
    public Guid? AgentId { get; set; }
    public int? InstanceSlot { get; set; }
    public bool CancelRequested { get; set; }
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? FinishedAt { get; set; }
    public string? Note { get; set; }

    /// <summary>Результат модуля (jsonb); формат знает модуль.</summary>
    public string? ResultJson { get; set; }

    public static bool IsFinal(JobStatus status) =>
        status is JobStatus.Succeeded or JobStatus.Warning or JobStatus.Failed or JobStatus.Cancelled;
}

public sealed class JobLogLine
{
    public long Id { get; set; }
    public Guid JobId { get; set; }
    public DateTimeOffset At { get; set; }
    public string Stage { get; set; } = "";
    public string Text { get; set; } = "";
}
