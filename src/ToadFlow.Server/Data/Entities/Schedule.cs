using ToadFlow.Contracts;

namespace ToadFlow.Server.Data.Entities;

public sealed class Schedule
{
    public Guid Id { get; set; }
    public string Name { get; set; } = "";
    public string ModuleKind { get; set; } = "";
    public Guid SourceId { get; set; }

    /// <summary>Папка результатов с подстановками <c>{дата}</c>, <c>{время}</c>, <c>{расписание}</c>.</summary>
    public string OutputFolderTemplate { get; set; } = "";

    public SelectionDto Selection { get; set; } = new([], []);

    /// <summary>Настройки модуля (jsonb); формат знает модуль.</summary>
    public string ModuleSettingsJson { get; set; } = "{}";

    public RecurrenceKind RecurrenceKind { get; set; }

    /// <summary>Дни недели битами: бит 0 — понедельник … бит 6 — воскресенье.</summary>
    public int DaysOfWeekMask { get; set; }

    public TimeOnly TimeOfDay { get; set; }
    public int? IntervalHours { get; set; }
    public DateTimeOffset? OnceAt { get; set; }

    /// <summary>1 — высокий, 0 — обычный, −1 — низкий.</summary>
    public int Priority { get; set; }

    public int ModelTimeoutMinutes { get; set; }
    public int MaxRetries { get; set; }
    public bool IsEnabled { get; set; }
    public DateTimeOffset? NextRunAt { get; set; }

    /// <summary>Последний полученный от агента список моделей — запасной, если агентов нет в сети.</summary>
    public List<ExpandedModelDto>? LastKnownModels { get; set; }

    public DateTimeOffset? LastKnownModelsAt { get; set; }
    public Guid CreatedByUserId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public Guid UpdatedByUserId { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

public sealed class ScheduleChange
{
    public Guid Id { get; set; }
    public Guid ScheduleId { get; set; }
    public DateTimeOffset At { get; set; }
    public Guid UserId { get; set; }
    public string Summary { get; set; } = "";
}
