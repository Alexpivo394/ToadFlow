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

    public Guid UpdatedByUserId { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>Пустая конфигурация: наборов нет, соединённые MEP не проверяются.</summary>
    public static readonly string EmptyDraft = ConfigurationJson.Serialize(new ClashConfiguration([], [], [], IgnoreConnectedMep: true));
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

public sealed record ClashPartSummary(Guid Id, string Name, string? Object, string? Part, int Models, int Sets, DateTimeOffset UpdatedAt);

public sealed record ClashPartView(Guid Id, string Name, string? Object, string? Part, DateTimeOffset UpdatedAt, IReadOnlyList<PartModelView> Models);
