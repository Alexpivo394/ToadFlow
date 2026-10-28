using System;

namespace ToadFlow.Contracts.Pipe;

/// <summary>
///     Сообщение по локальному пайпу между агентом и аддином: одна строка JSON, поле <c>type</c> — вид сообщения,
///     имена в camelCase, перечисления строками. Сериализует каждая сторона своей библиотекой.
/// </summary>
public abstract class PipeMessage
{
    public abstract string Type { get; }
}

/// <summary>Аддин → агент: аддин загружен в Revit и подключился. Revit при этом может ещё запускаться — см. <see cref="ReadyMessage" />.</summary>
public sealed class HelloMessage : PipeMessage
{
    public const string Kind = "hello";
    public override string Type => Kind;
    public Guid InstanceId { get; set; }
    public int RevitYear { get; set; }
    public int Pid { get; set; }
    public bool HasNwcExporter { get; set; }
    public string AddinVersion { get; set; } = "";

    /// <summary>Журнал этого процесса Revit — для разбора сбоев. null — аддин до 1.6 или Revit не сообщил путь.</summary>
    public string? JournalPath { get; set; }

    /// <summary>Аддин пришлёт <see cref="ReadyMessage" />, когда Revit закончит запуск. false — аддин до 1.7: готов сразу после hello.</summary>
    public bool SendsReady { get; set; }
}

/// <summary>Аддин → агент: Revit закончил запуск (ApplicationInitialized) и готов выполнять задания.</summary>
public sealed class ReadyMessage : PipeMessage
{
    public const string Kind = "ready";
    public override string Type => Kind;
}

/// <summary>Агент → аддин: выполнить задание над моделью.</summary>
public sealed class JobMessage : PipeMessage
{
    public const string Kind = "job";
    public override string Type => Kind;
    public Guid JobId { get; set; }

    /// <summary>Вид задания (<see cref="ModuleKinds" />). Пусто — выгрузка: так шлёт задания агент до 1.1.</summary>
    public string ModuleKind { get; set; } = "";

    public string PayloadJson { get; set; } = "";
    public int TimeoutMinutes { get; set; }

    /// <summary>У задания остались попытки: сбой, который в новом Revit может не повториться, лучше вернуть повтором. false — агент до 1.7.1.</summary>
    public bool CanRetry { get; set; }
}

/// <summary>Аддин → агент: шаг выгрузки или строка журнала.</summary>
public sealed class ProgressMessage : PipeMessage
{
    public const string Kind = "progress";
    public override string Type => Kind;
    public Guid JobId { get; set; }
    public string Stage { get; set; } = "";
    public string Text { get; set; } = "";
    public DateTimeOffset At { get; set; }
}

/// <summary>
///     Аддин → агент, раз в несколько секунд: когда поток Revit API последний раз что-то делал. Фоновый поток аддина
///     жив, даже если Revit завис, поэтому агент судит о зависании по <see cref="LastActivityAt" />, а не по приходу сообщения.
/// </summary>
public sealed class AliveMessage : PipeMessage
{
    public const string Kind = "alive";
    public override string Type => Kind;
    public DateTimeOffset LastActivityAt { get; set; }
}

/// <summary>Аддин → агент: модель обработана.</summary>
public sealed class ResultMessage : PipeMessage
{
    public const string Kind = "result";
    public override string Type => Kind;
    public Guid JobId { get; set; }
    public JobOutcome Outcome { get; set; }
    public string? Note { get; set; }
    public string? ResultJson { get; set; }

    /// <summary>
    ///     Состояние Revit после задания под сомнением (модель не закрылась, неожиданное исключение): агент не даёт этому
    ///     экземпляру новых заданий и перезапускает его. null — экземпляр можно использовать дальше.
    /// </summary>
    public string? RecycleReason { get; set; }
}

/// <summary>Агент → аддин: бросить модель (отмена пользователем).</summary>
public sealed class CancelMessage : PipeMessage
{
    public const string Kind = "cancel";
    public override string Type => Kind;
    public Guid JobId { get; set; }
}

public static class PipeNames
{
    /// <summary>Переменная окружения, с которой агент запускает Revit. Без неё аддин ничего не делает.</summary>
    public const string InstanceIdVariable = "TOADFLOW_INSTANCE_ID";

    public static string ForInstance(Guid instanceId) => $"toadflow-revit-{instanceId:N}";
}
