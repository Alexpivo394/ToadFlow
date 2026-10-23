using ToadFlow.Server.Data.Entities;

namespace ToadFlow.Server.Events;

/// <summary>Кто сделал: пользователь, агент или сама система.</summary>
public sealed record Actor(ActorKind Kind, string Name)
{
    public static readonly Actor System = new(ActorKind.System, "Система");
}

/// <summary>
///     Значимое событие. Его получают «События системы» (если есть <see cref="Describe" />), живые обновления UI
///     (если есть <see cref="UiEntity" />) и, в будущем, модуль уведомлений.
/// </summary>
public abstract record DomainEvent(Actor Actor)
{
    public DateTimeOffset At { get; init; }

    /// <summary>Текст для «Событий системы»; null — событие слишком частое, не пишем.</summary>
    public virtual string? Describe() => null;

    /// <summary>Какой список в интерфейсе обновить: agents, jobs, runs, schedules, settings, users, events, presets, sources.</summary>
    public abstract string UiEntity { get; }

    public abstract string UiId { get; }
}

public sealed record AgentWentOnline(Actor Actor, Guid AgentId, string MachineName) : DomainEvent(Actor)
{
    public override string Describe() => $"Агент {MachineName} подключился";
    public override string UiEntity => "agents";
    public override string UiId => AgentId.ToString();
}

public sealed record AgentWentOffline(Actor Actor, Guid AgentId, string MachineName) : DomainEvent(Actor)
{
    public override string Describe() => $"Агент {MachineName} потерял связь";
    public override string UiEntity => "agents";
    public override string UiId => AgentId.ToString();
}

/// <param name="Reason">null — убит вручную; иначе причина автоматического убийства (таймаут, молчание).</param>
public sealed record InstanceKilled(Actor Actor, Guid AgentId, string MachineName, int Slot, string? Reason) : DomainEvent(Actor)
{
    public override string Describe() => Reason is null
        ? $"Вручную убит Revit #{Slot} на {MachineName}"
        : $"Revit #{Slot} на {MachineName} убит: {Reason}";

    public override string UiEntity => "agents";
    public override string UiId => AgentId.ToString();
}

public sealed record AgentChanged(Actor Actor, Guid AgentId, string Text) : DomainEvent(Actor)
{
    public override string Describe() => Text;
    public override string UiEntity => "agents";
    public override string UiId => AgentId.ToString();
}

public sealed record JobChanged(Actor Actor, Guid JobId, Guid RunId) : DomainEvent(Actor)
{
    public override string UiEntity => "jobs";
    public override string UiId => JobId.ToString();
}

public sealed record RunChanged(Actor Actor, Guid RunId) : DomainEvent(Actor)
{
    public override string UiEntity => "runs";
    public override string UiId => RunId.ToString();
}

public sealed record RunFinished(Actor Actor, Guid RunId, string Name, int Succeeded, int Warnings, int Failed, int Cancelled)
    : DomainEvent(Actor)
{
    public override string Describe() =>
        $"Запуск «{Name}» завершён: успешно {Succeeded}, с предупреждением {Warnings}, с ошибкой {Failed}" +
        (Cancelled > 0 ? $", отменено {Cancelled}" : "");

    public override string UiEntity => "runs";
    public override string UiId => RunId.ToString();
}

public sealed record ScheduleChanged(Actor Actor, Guid ScheduleId, string Name, string Summary) : DomainEvent(Actor)
{
    public override string Describe() => $"Расписание «{Name}»: {Summary}";
    public override string UiEntity => "schedules";
    public override string UiId => ScheduleId.ToString();
}

public sealed record SettingsChanged(Actor Actor, string Summary) : DomainEvent(Actor)
{
    public override string Describe() => $"Общие настройки: {Summary}";
    public override string UiEntity => "settings";
    public override string UiId => "";
}

public sealed record UserChanged(Actor Actor, Guid UserId, string Text) : DomainEvent(Actor)
{
    public override string Describe() => Text;
    public override string UiEntity => "users";
    public override string UiId => UserId.ToString();
}

/// <summary>Изменился справочник (источники, пресеты) — в журнал с текстом, в UI — обновить список.</summary>
public sealed record CatalogChanged(Actor Actor, string Entity, Guid Id, string Text) : DomainEvent(Actor)
{
    public override string Describe() => Text;
    public override string UiEntity => Entity;
    public override string UiId => Id.ToString();
}

/// <summary>Пришло состояние агента и его экземпляров — дашборду пора обновиться. В журнал не пишется.</summary>
public sealed record AgentStatusReceived(Actor Actor, Guid AgentId) : DomainEvent(Actor)
{
    public override string UiEntity => "agents";
    public override string UiId => AgentId.ToString();
}
