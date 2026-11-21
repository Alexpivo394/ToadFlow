using Microsoft.EntityFrameworkCore;
using ToadFlow.Server.Auth;
using ToadFlow.Server.Data;
using ToadFlow.Server.Families.Search;
using ToadFlow.Server.Settings;

namespace ToadFlow.Server.Families.Requests;

/// <param name="IsMember">Текущий пользователь — автор или присоединился.</param>
/// <param name="Unread">Есть изменения, которых текущий участник ещё не видел.</param>
/// <param name="Urgency">Для панели ToadTools 4.4: <c>Urgent</c>, если заявка блокирует выдачу.</param>
/// <param name="Turn">Чей ход: <c>staff</c> — координатора, <c>author</c> — автора и участников; null — заявка закрыта.</param>
/// <param name="Overdue">Открыта, а «нужно к» уже прошло.</param>
/// <param name="WaitingDays">Сколько полных дней заявка открыта (у закрытой — сколько была открыта).</param>
/// <param name="ThumbnailFileId">Первая картинка заявки — миниатюра в очереди.</param>
public sealed record RequestListItem(
    Guid Id,
    int Number,
    string Title,
    string Section,
    RequestUrgency Urgency,
    string Project,
    RequestState State,
    Guid AuthorId,
    string AuthorName,
    Guid? AssigneeId,
    string? AssigneeName,
    Guid? CardId,
    string? CardName,
    int Members,
    bool IsMember,
    bool Unread,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    RequestKind Kind,
    Guid? DirectionId,
    string? DirectionName,
    DateOnly? NeededBy,
    DateOnly? PromisedBy,
    bool BlocksIssue,
    bool Overdue,
    string? Turn,
    int WaitingDays,
    Guid? ThumbnailFileId,
    int? RevitYear,
    Guid? TargetCardId,
    string? TargetCardName,
    RequestResolution? Resolution,
    DateTimeOffset? ReviewSince);

/// <param name="FileIds">Вложения сообщения.</param>
public sealed record RequestMessageView(Guid Id, Guid? AuthorId, string? AuthorName, string Text, bool IsEvent, DateTimeOffset At, IReadOnlyList<Guid> FileIds);

/// <param name="MessageId">Сообщение, к которому приложен файл; null — к самой заявке.</param>
/// <param name="Inline">Браузер может показать файл сам (картинка, PDF).</param>
public sealed record RequestFileView(Guid Id, string FileName, long Size, string ContentType, string UploadedByName, DateTimeOffset At, Guid? MessageId,
    Guid UploadedBy, bool Inline);

public sealed record RequestMemberView(Guid UserId, string Name);

/// <param name="AutoAcceptAt">На приёмке — когда заявка примется сама.</param>
public sealed record RequestView(
    RequestListItem Request,
    string Text,
    string? Reason,
    IReadOnlyList<RequestMemberView> Members,
    IReadOnlyList<RequestMessageView> Messages,
    IReadOnlyList<RequestFileView> Files,
    DateTimeOffset? AutoAcceptAt);

/// <param name="Urgency">Прежнее поле (панель ToadTools 4.4): <c>Urgent</c> — то же, что <paramref name="BlocksIssue" />.</param>
/// <param name="DirectionId">Направление явно; null — по разделу и профилю автора.</param>
/// <param name="TargetCardId">Обязательно для <see cref="RequestKind.Fix" />.</param>
/// <param name="FileIds">Свои «ничейные» загрузки — приложить к заявке.</param>
public sealed record NewRequest(
    string Title,
    string? Text,
    string? Section,
    RequestUrgency? Urgency,
    string? Project,
    RequestKind? Kind = null,
    Guid? DirectionId = null,
    DateOnly? NeededBy = null,
    bool? BlocksIssue = null,
    int? RevitYear = null,
    Guid? TargetCardId = null,
    Guid[]? FileIds = null);

public sealed record RequestEdit(string Title, string? Text, string? Section, string? Project, DateOnly? NeededBy, bool BlocksIssue, int? RevitYear);

/// <param name="FileIds">Свои «ничейные» загрузки — приложить к сообщению.</param>
public sealed record RequestMessageRequest(string? Text, Guid[]? FileIds = null);

public sealed record RequestAssign(Guid UserId, DateOnly? PromisedBy = null);

public sealed record RequestTake(DateOnly? PromisedBy);

public sealed record RequestCard(Guid CardId);

public sealed record RequestReject(string Reason);

public sealed record SimilarResult(IReadOnlyList<FamilyHit> Families, IReadOnlyList<RequestListItem> Requests);

public sealed record RequestAssignee(Guid Id, string Name);

/// <param name="MedianCloseHours">Медиана от подачи до приёмки по принятым за 30 дней; null — таких нет.</param>
/// <param name="MineDirection">Открытых в направлениях координатора (вкладка «Моё направление»).</param>
public sealed record RequestStats(int Open, int Overdue, int Unassigned, double? MedianCloseHours, int MineDirection);

/// <summary>
///     Заявки на семейства. Читают все участники; подают, пишут и присоединяются проектировщик, координатор и администратор; ведут
///     заявку (взять, передать, закрыть) — координатор и администратор. Части: <c>RequestQueries</c> — списки и просмотр,
///     <c>RequestWorkflow</c> — изменения, <c>RequestFiles</c> — вложения.
/// </summary>
internal static partial class RequestEndpoints
{
    public const int MaxTitle = 200;
    public const int MaxText = 8000;
    public const int MaxProject = 200;
    public const int MaxSection = 100;
    private const int SimilarCount = 5;

    public static void MapFamilyRequests(this RouteGroupBuilder families)
    {
        var requests = families.MapGroup("/requests");
        requests.MapGet("", ListAsync);
        requests.MapGet("/updates", UpdatesAsync);
        requests.MapGet("/similar", SimilarAsync);
        requests.MapGet("/projects", ProjectsAsync);
        requests.MapGet("/stats", StatsAsync).RequireAuthorization(Policies.Operator);
        requests.MapGet("/assignees", AssigneesAsync).RequireAuthorization(Policies.Operator);
        requests.MapGet("/{id:guid}", GetAsync);
        requests.MapPost("/{id:guid}/seen", SeenAsync);

        requests.MapPost("", CreateAsync).RequireAuthorization(Policies.Designer);
        requests.MapPut("/{id:guid}", EditAsync).RequireAuthorization(Policies.Designer);
        requests.MapPost("/{id:guid}/join", JoinAsync).RequireAuthorization(Policies.Designer);
        requests.MapPost("/{id:guid}/leave", LeaveAsync).RequireAuthorization(Policies.Designer);
        requests.MapPost("/{id:guid}/messages", MessageAsync).RequireAuthorization(Policies.Designer);

        requests.MapPost("/{id:guid}/take", TakeAsync).RequireAuthorization(Policies.Operator);
        requests.MapPost("/{id:guid}/assign", AssignAsync).RequireAuthorization(Policies.Operator);
        requests.MapPost("/{id:guid}/done", DoneAsync).RequireAuthorization(Policies.Operator);
        requests.MapPost("/{id:guid}/exists", ExistsAsync).RequireAuthorization(Policies.Operator);
        requests.MapPost("/{id:guid}/reject", RejectAsync).RequireAuthorization(Policies.Operator);
        requests.MapPost("/{id:guid}/reopen", ReopenAsync).RequireAuthorization(Policies.Operator);

        MapFiles(requests);
    }

    /// <summary>Сегодня в часовом поясе сервера (общие настройки): от него считается просрочка.</summary>
    internal static async Task<DateOnly> TodayAsync(ISettingsStore settings, TimeProvider time, CancellationToken ct)
    {
        var zone = (await settings.GetAsync(ct)).TimeZone;
        return DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(time.GetUtcNow(), zone).DateTime);
    }

    private static async Task<Dictionary<Guid, string>> NamesAsync(ToadFlowDb db, IEnumerable<Guid> ids, CancellationToken ct)
    {
        var list = ids.Distinct().ToList();
        return await db.Users.AsNoTracking().Where(u => list.Contains(u.Id)).ToDictionaryAsync(u => u.Id, u => u.DisplayName, ct);
    }

    private static string Search(params string[] parts) => string.Join(' ', parts.Where(p => p.Length > 0)).ToLowerInvariant();

    private static List<string> Words(string? q) =>
        (q ?? "").ToLowerInvariant().Split([' ', ',', ';', '.'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Distinct().Take(10).ToList();

    private static string Escape(string word) => word.Replace(@"\", @"\\").Replace("%", @"\%").Replace("_", @"\_");

    /// <summary>Хотя бы одно слово входит в поле: <c>LIKE</c> через ИЛИ, параметрами запроса.</summary>
    private static System.Linq.Expressions.Expression<Func<T, bool>> AnyWord<T>(IReadOnlyList<string> words, string property)
    {
        var item = System.Linq.Expressions.Expression.Parameter(typeof(T), "x");
        var like = typeof(DbFunctionsExtensions).GetMethod(nameof(DbFunctionsExtensions.Like), [typeof(DbFunctions), typeof(string), typeof(string), typeof(string)])!;
        System.Linq.Expressions.Expression body = System.Linq.Expressions.Expression.Constant(false);
        foreach (var word in words)
        {
            var value = System.Linq.Expressions.Expression.Property(System.Linq.Expressions.Expression.Constant(new Box("%" + Escape(word) + "%")), nameof(Box.Value));
            body = System.Linq.Expressions.Expression.OrElse(body, System.Linq.Expressions.Expression.Call(like, System.Linq.Expressions.Expression.Constant(EF.Functions),
                System.Linq.Expressions.Expression.Property(item, property), value, System.Linq.Expressions.Expression.Constant(@"\")));
        }

        return System.Linq.Expressions.Expression.Lambda<Func<T, bool>>(body, item);
    }

    private sealed class Box(string value)
    {
        public string Value { get; } = value;
    }
}
