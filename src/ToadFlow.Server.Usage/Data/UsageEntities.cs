namespace ToadFlow.Server.Usage.Data;

// Таблицы аналитики. Внешних ключей на users нет, как у аналитики LOI: удаление учётки не трогает накопленные цифры.

/// <summary>Компьютер проектировщика: последнее присланное состояние железа. Ключ — имя компьютера в нижнем регистре.</summary>
public sealed class UsageMachine
{
    public string Key { get; set; } = "";
    public string Name { get; set; } = "";
    public string? Cpu { get; set; }
    public int? Cores { get; set; }
    public int? RamMb { get; set; }
    public string? Gpu { get; set; }
    public int? GpuMemoryMb { get; set; }

    /// <summary><c>Ssd</c>, <c>Hdd</c> или <c>Unknown</c>.</summary>
    public string DiskKind { get; set; } = "Unknown";

    public int? DiskTotalGb { get; set; }
    public int? DiskFreeGb { get; set; }
    public string? Os { get; set; }
    public int[] RevitYears { get; set; } = [];
    public string? ToadTools { get; set; }
    public DateTimeOffset FirstSeenAt { get; set; }
    public DateTimeOffset LastSeenAt { get; set; }
    public Guid? LastUserId { get; set; }
}

/// <summary>Один запуск Revit. Нарастающий итог: сборщик присылает его снова, пока сессия идёт, — строка заменяется.</summary>
public sealed class UsageSession
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public string MachineKey { get; set; } = "";
    public string? WindowsUser { get; set; }
    public int RevitYear { get; set; }
    public string? RevitBuild { get; set; }
    public string? ToadTools { get; set; }
    public DateTimeOffset StartedAt { get; set; }

    /// <summary>Последняя отметка сборщика; у аварийной сессии — её конец.</summary>
    public DateTimeOffset SeenAt { get; set; }

    public int ActiveSeconds { get; set; }

    /// <summary><c>Running</c>, <c>Normal</c> или <c>Abnormal</c>.</summary>
    public string End { get; set; } = "Running";

    public int? PeakMemoryMb { get; set; }
    public DateTimeOffset ReceivedAt { get; set; }
}

/// <summary>Активное время сессии за местный день машины.</summary>
public sealed class UsageSessionDay
{
    public Guid SessionId { get; set; }
    public DateOnly Day { get; set; }
    public Guid UserId { get; set; }
    public string MachineKey { get; set; } = "";
    public int ActiveSeconds { get; set; }
}

/// <summary>Одна операция с моделью — то, чего человек ждёт: открытие, связь, синхронизация, сохранение, обновление.</summary>
public sealed class UsageOperation
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public Guid SessionId { get; set; }
    public string MachineKey { get; set; } = "";

    /// <summary><c>Open</c>, <c>Link</c>, <c>Sync</c>, <c>Save</c> или <c>Reload</c>.</summary>
    public string Kind { get; set; } = "";

    public DateTimeOffset StartedAt { get; set; }
    public long DurationMs { get; set; }

    /// <summary><c>Succeeded</c>, <c>Failed</c> или <c>Cancelled</c>.</summary>
    public string Outcome { get; set; } = "";

    public string ModelName { get; set; } = "";
    public string ModelPath { get; set; } = "";

    /// <summary>Путь для сравнения: нижний регистр, <c>/</c> вместо <c>\</c>.</summary>
    public string ModelKey { get; set; } = "";

    /// <summary><c>File</c>, <c>RevitServer</c> или <c>Cloud</c>.</summary>
    public string Storage { get; set; } = "";

    public string? ServerHost { get; set; }

    /// <summary>У связи — ключ модели, в которую она загружена.</summary>
    public string? HostModelKey { get; set; }

    public long? FileSizeBytes { get; set; }
    public int? FreeMemoryMb { get; set; }
    public int RevitYear { get; set; }
    public DateTimeOffset ReceivedAt { get; set; }
}

/// <summary>Запуски одной команды за местный день в одной сессии.</summary>
public sealed class UsageCommandDay
{
    public Guid SessionId { get; set; }
    public DateOnly Day { get; set; }

    /// <summary><c>ToadTools</c>, <c>Revit</c> или <c>Addin</c>.</summary>
    public string Source { get; set; } = "";

    public string CommandId { get; set; } = "";
    public Guid UserId { get; set; }
    public string MachineKey { get; set; } = "";
    public string Name { get; set; } = "";
    public string? Tab { get; set; }
    public int Count { get; set; }
    public int Failed { get; set; }
    public int Cancelled { get; set; }
    public long DurationMs { get; set; }
}

/// <summary>Переходы человека на страницу веба за день.</summary>
public sealed class UsageWebDay
{
    public Guid UserId { get; set; }
    public DateOnly Day { get; set; }
    public string Space { get; set; } = "";
    public string Page { get; set; } = "";
    public int Count { get; set; }
}

/// <summary>Вызовы API плагинами (по личному токену) за день, по разделу API.</summary>
public sealed class UsagePluginDay
{
    public Guid UserId { get; set; }
    public DateOnly Day { get; set; }
    public string Area { get; set; } = "";
    public int Count { get; set; }
}
