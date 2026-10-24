namespace ToadFlow.Server.Loi.Analytics;

public enum LoiCheckSource
{
    /// <summary>Задание расписания ToadFlow.</summary>
    Schedule,

    /// <summary>Ручная проверка в плагине ParamChecker, присланная с личным токеном.</summary>
    Manual
}

/// <summary>
///     Проверка одной модели для аналитики: итоги модели и куда она относится (заказчик, объект, раздел, часть). Классификация
///     записывается вместе с проверкой, чтобы фильтры не разбирали имена на лету; после правки справочников её пересчитывают.
/// </summary>
public sealed class LoiCheck
{
    /// <summary>У проверки по расписанию — id задания, у ручной — id, который выдал плагин: повторная отправка не задвоит её.</summary>
    public Guid Id { get; set; }

    public DateTimeOffset CheckedAt { get; set; }
    public LoiCheckSource Source { get; set; }
    public Guid? RunId { get; set; }
    public Guid? ScheduleId { get; set; }

    /// <summary>Кто проверял вручную; у проверки по расписанию null.</summary>
    public Guid? UserId { get; set; }

    /// <summary>Путь модели для сравнения (<see cref="CheckRecorder.ModelKey" />) — по нему строится история модели.</summary>
    public string ModelKey { get; set; } = "";

    public string ModelName { get; set; } = "";
    public string ModelPath { get; set; } = "";
    public string? Customer { get; set; }
    public string? Object { get; set; }
    public string? Discipline { get; set; }
    public string? Part { get; set; }

    /// <summary>Свои поля справочника именования, например «Стадия».</summary>
    public Dictionary<string, string> Fields { get; set; } = [];

    public string ProfileName { get; set; } = "";
    public string ProfileFingerprint { get; set; } = "";
    public string ViewName { get; set; } = "";
    public double DurationSeconds { get; set; }
    public int Elements { get; set; }
    public int CompleteElements { get; set; }
    public int Values { get; set; }
    public int Filled { get; set; }
    public int Empty { get; set; }
    public int Missing { get; set; }

    public List<LoiCheckRule> Rules { get; set; } = [];
}

public sealed class LoiCheckRule
{
    public long Id { get; set; }
    public Guid CheckId { get; set; }

    /// <summary>Порядок правила в профиле.</summary>
    public int Position { get; set; }

    public string Title { get; set; } = "";
    public int Elements { get; set; }
    public int CompleteElements { get; set; }
    public int Values { get; set; }
    public int Filled { get; set; }
    public int Empty { get; set; }
    public int Missing { get; set; }

    public List<LoiCheckParameter> Parameters { get; set; } = [];
}

/// <summary>Параметр правила в проверке. Id проверки продублирован, чтобы срезы по параметрам не шли через правила.</summary>
public sealed class LoiCheckParameter
{
    public long Id { get; set; }
    public long RuleId { get; set; }
    public Guid CheckId { get; set; }
    public string Name { get; set; } = "";
    public int Filled { get; set; }
    public int Empty { get; set; }
    public int Missing { get; set; }
}

/// <summary>Ручная поправка классификации модели — главнее справочника именования. Пустое поле — как по справочнику.</summary>
public sealed class LoiModelOverride
{
    public string ModelKey { get; set; } = "";
    public string ModelName { get; set; } = "";
    public string? Customer { get; set; }
    public string? Object { get; set; }
    public string? Discipline { get; set; }
    public string? Part { get; set; }
    public Guid UpdatedByUserId { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}
