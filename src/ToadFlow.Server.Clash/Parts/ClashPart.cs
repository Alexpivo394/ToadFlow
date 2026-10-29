using Alexpivo394.Revit.Clash.Rules;

namespace ToadFlow.Server.Clash.Parts;

/// <summary>
///     Часть проекта для проверки коллизий: объект и часть здания из разбора имён и модели, которые проверяются вместе.
///     Наборы, матрица и исключения части — её черновик (<see cref="DraftJson" />).
/// </summary>
public sealed class ClashPart
{
    public Guid Id { get; set; }
    public string Name { get; set; } = "";
    public string? Object { get; set; }
    public string? Part { get; set; }

    /// <summary>Черновик конфигурации в формате <see cref="ConfigurationJson" />.</summary>
    public string DraftJson { get; set; } = EmptyDraft;

    /// <summary>Версия черновика: растёт на каждом сохранении; по ней сливаются правки нескольких пользователей.</summary>
    public int DraftVersion { get; set; }

    /// <summary>Прогон, чьи коллизии — нынешний результат черновика; null — удачных прогонов не было.</summary>
    public Guid? ResultRunId { get; set; }

    /// <summary>Последняя утверждённая версия (<see cref="ClashPartVersion" />); null — часть не утверждалась.</summary>
    public int? ApprovedVersion { get; set; }

    /// <summary>Последний удачный прогон утверждённой версии: основа следующего прогона; null — не было.</summary>
    public Guid? ApprovedRunId { get; set; }

    /// <summary>Номер следующей новой коллизии части: номера выдаются один раз и не меняются.</summary>
    public int NextCollisionNumber { get; set; } = 1;

    public Guid UpdatedByUserId { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>Пустая конфигурация: наборов нет, соединённые MEP не проверяются.</summary>
    public static readonly string EmptyDraft = ConfigurationJson.Serialize(new ClashConfiguration([], [], [], [], IgnoreConnectedMep: true));
}

/// <summary>Модель в части. Раздел — из разбора имени модели, если не задан вручную.</summary>
public sealed class ClashPartModel
{
    public Guid PartId { get; set; }
    public Guid ModelId { get; set; }

    /// <summary>Раздел, заданный вручную; null — как в разборе имени.</summary>
    public string? DisciplineOverride { get; set; }
}

public sealed record ClashPartRequest(string Name, string? Object = null, string? Part = null);

/// <summary>С чего начать черновик новой части.</summary>
/// <param name="Kind"><c>disciplines</c> — группа и набор «Всё» на раздел, полная матрица 20 мм; <c>template</c>; <c>part</c> — копия другой части; <c>empty</c>.</param>
public sealed record PartStart(string Kind, Guid? TemplateId = null, Guid? PartId = null);

public sealed record ClashPartCreateRequest(string Name, string? Object = null, string? Part = null, PartModelRequest[]? Models = null, PartStart? Start = null);

public sealed record PartModelRequest(Guid ModelId, string? DisciplineOverride);

/// <param name="Discipline">Раздел модели в проверке: поправка или разбор имени; null — не определён, модель не проверяется.</param>
public sealed record PartModelView(
    Guid ModelId,
    string Name,
    string ModelPath,
    string? ParsedDiscipline,
    string? DisciplineOverride,
    string? Discipline,
    ClashSnapshotView? Latest);

/// <param name="Groups">Группы черновика.</param>
/// <param name="Warnings">Модели без выгрузки или с устаревшей выгрузкой: «ЭОМ: нет выгрузки».</param>
/// <param name="LastRun">Последний прогон черновика; null — не прогонялся.</param>
/// <param name="ApprovedVersion">Последняя утверждённая версия; null — часть не утверждалась.</param>
public sealed record ClashPartSummary(Guid Id, string Name, string? Object, string? Part, int Models, int Sets, DateTimeOffset UpdatedAt,
    IReadOnlyList<string> Groups, IReadOnlyList<string> Warnings, Engine.DraftRunSummary? LastRun, int? ApprovedVersion = null);

public sealed record ClashPartView(Guid Id, string Name, string? Object, string? Part, DateTimeOffset UpdatedAt, IReadOnlyList<PartModelView> Models);

/// <summary>Сохранённая версия черновика части — база для слияния правок, начатых с неё. Хранятся последние <see cref="Keep" />.</summary>
public sealed class ClashDraftVersion
{
    public const int Keep = 100;

    public Guid PartId { get; set; }
    public int Version { get; set; }
    public string ConfigurationJson { get; set; } = "";
    public Guid UserId { get; set; }
    public DateTimeOffset SavedAt { get; set; }
}
