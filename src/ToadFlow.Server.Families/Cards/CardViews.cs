using Alexpivo394.Revit.Families.Files;
using ToadFlow.Server.Families.Data;

namespace ToadFlow.Server.Families.Cards;

/// <param name="IsCurrent">Действующая версия карточки.</param>
public sealed record FamilyVersionView(
    Guid Id,
    string Hash,
    int? RevitYear,
    VersionState State,
    string Note,
    string RelativePath,
    DateTimeOffset SeenAt,
    ReadState ReadState,
    string? ReadError,
    bool HasPreview,
    bool IsCurrent,
    string? AuthorName,
    string? Comment);

/// <summary>Карточка семейства с содержимым действующей версии и историей версий (новые первыми).</summary>
/// <param name="FullPath">Где лежит файл: корень библиотеки и путь от него.</param>
/// <param name="Content">Содержимое действующей версии; null — ферма ещё не прочитала или не смогла.</param>
public sealed record FamilyCardView(
    Guid Id,
    string Name,
    string RelativePath,
    string FullPath,
    string Section,
    CardState State,
    string Category,
    string Placement,
    int? RevitYear,
    int TypeCount,
    string? Description,
    IReadOnlyList<string> Tags,
    string? Manufacturer,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    DateTimeOffset? ArchivedAt,
    Guid? CurrentVersionId,
    ReadState? CurrentReadState,
    string? CurrentReadError,
    bool HasPreview,
    FamilyContent? Content,
    IReadOnlyList<FamilyVersionView> Versions,
    int LoadCount,
    DateTimeOffset? LastLoadedAt);

/// <summary>Что лежит в папке проверки: новое семейство или версия-кандидат утверждённого.</summary>
public sealed record FamilyReviewItem(Guid CardId, Guid VersionId, string Name, string RelativePath, bool IsNew, int? RevitYear, DateTimeOffset SeenAt);

public sealed record FamilyIssueView(
    Guid Id,
    IssueKind Kind,
    Guid? CardId,
    string? CardName,
    string RelativePath,
    string Text,
    DateTimeOffset CreatedAt,
    DateTimeOffset? ResolvedAt);

/// <summary>Что координатор может дописать к карточке руками; null — поле очищается.</summary>
public sealed record FamilyNotesRequest(string? Description, IReadOnlyList<string>? Tags, string? Manufacturer);

/// <summary>Состояние библиотеки для «Параметров» и «Обзора».</summary>
/// <param name="AgentName">Агент библиотеки: назначенный или тот, кого сервер выберет сейчас; null — подходящего нет.</param>
/// <param name="LastScanAt">Когда пришёл отчёт последнего обхода.</param>
/// <param name="PendingReads">Версии, которые ждут чтения фермой.</param>
public sealed record LibraryStatus(
    bool Configured,
    string? AgentName,
    bool AgentOnline,
    DateTimeOffset? LastScanAt,
    bool? LastScanComplete,
    IReadOnlyList<string> LastScanErrors,
    int PendingReads,
    int FailedReads);
