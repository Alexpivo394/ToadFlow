using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using ToadFlow.Contracts;
using ToadFlow.Server.Auth;
using ToadFlow.Server.Data;
using ToadFlow.Server.Events;
using ToadFlow.Server.Families.Data;
using ToadFlow.Server.Families.Search;
using ToadFlow.Server.Runs;

namespace ToadFlow.Server.Families.Requests;

/// <param name="IsMember">Текущий пользователь — автор или присоединился.</param>
/// <param name="Unread">Есть изменения, которых текущий участник ещё не видел.</param>
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
    DateTimeOffset UpdatedAt);

public sealed record RequestMessageView(Guid Id, Guid? AuthorId, string? AuthorName, string Text, bool IsEvent, DateTimeOffset At);

public sealed record RequestFileView(Guid Id, string FileName, long Size, string ContentType, string UploadedByName, DateTimeOffset At);

public sealed record RequestMemberView(Guid UserId, string Name);

public sealed record RequestView(
    RequestListItem Request,
    string Text,
    string? Reason,
    IReadOnlyList<RequestMemberView> Members,
    IReadOnlyList<RequestMessageView> Messages,
    IReadOnlyList<RequestFileView> Files);

public sealed record NewRequest(string Title, string? Text, string? Section, RequestUrgency Urgency, string? Project);

public sealed record RequestMessageRequest(string Text);

public sealed record RequestAssign(Guid UserId);

public sealed record RequestCard(Guid CardId);

public sealed record RequestReject(string Reason);

public sealed record SimilarResult(IReadOnlyList<FamilyHit> Families, IReadOnlyList<RequestListItem> Requests);

public sealed record RequestAssignee(Guid Id, string Name);

/// <summary>
///     Заявки на семейства. Читают все участники; подают, пишут и присоединяются проектировщик, координатор и администратор; ведут
///     заявку (взять, передать, закрыть) — координатор и администратор.
/// </summary>
internal static class RequestEndpoints
{
    public const int MaxTitle = 200;
    public const int MaxText = 4000;
    public const long MaxFileSize = 25L * 1024 * 1024;
    public const int MaxFiles = 10;
    private const int SimilarCount = 5;

    public static void MapFamilyRequests(this RouteGroupBuilder families)
    {
        var requests = families.MapGroup("/requests");
        requests.MapGet("", ListAsync);
        requests.MapGet("/updates", UpdatesAsync);
        requests.MapGet("/similar", SimilarAsync);
        requests.MapGet("/assignees", AssigneesAsync).RequireAuthorization(Policies.Operator);
        requests.MapGet("/{id:guid}", GetAsync);
        requests.MapGet("/{id:guid}/files/{fileId:guid}", FileAsync);
        requests.MapPost("/{id:guid}/seen", SeenAsync);

        requests.MapPost("", CreateAsync).RequireAuthorization(Policies.Designer);
        requests.MapPost("/{id:guid}/join", JoinAsync).RequireAuthorization(Policies.Designer);
        requests.MapPost("/{id:guid}/leave", LeaveAsync).RequireAuthorization(Policies.Designer);
        requests.MapPost("/{id:guid}/messages", MessageAsync).RequireAuthorization(Policies.Designer);
        requests.MapPost("/{id:guid}/files", UploadAsync).RequireAuthorization(Policies.Designer);

        requests.MapPost("/{id:guid}/take", TakeAsync).RequireAuthorization(Policies.Operator);
        requests.MapPost("/{id:guid}/assign", AssignAsync).RequireAuthorization(Policies.Operator);
        requests.MapPost("/{id:guid}/done", DoneAsync).RequireAuthorization(Policies.Operator);
        requests.MapPost("/{id:guid}/exists", ExistsAsync).RequireAuthorization(Policies.Operator);
        requests.MapPost("/{id:guid}/reject", RejectAsync).RequireAuthorization(Policies.Operator);
        requests.MapPost("/{id:guid}/reopen", ReopenAsync).RequireAuthorization(Policies.Operator);
    }

    private static async Task<Page<RequestListItem>> ListAsync(ToadFlowDb db, ClaimsPrincipal user, string? scope, string? state, string? q, int? page, int? pageSize,
        CancellationToken ct)
    {
        var me = user.UserId();
        var requests = db.Set<FamilyRequest>().AsNoTracking();
        var members = db.Set<FamilyRequestMember>();
        if (scope != "all") requests = requests.Where(r => members.Any(m => m.RequestId == r.Id && m.UserId == me) || r.AssigneeUserId == me);
        var states = (state ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(s => Enum.TryParse<RequestState>(s, true, out var parsed) ? parsed : (RequestState?)null).OfType<RequestState>().ToList();
        if (states.Count > 0) requests = requests.Where(r => states.Contains(r.State));
        foreach (var word in Words(q)) requests = requests.Where(r => EF.Functions.Like(r.SearchText, "%" + Escape(word) + "%", @"\"));

        var total = await requests.CountAsync(ct);
        var size = Math.Clamp(pageSize ?? 50, 1, 200);
        var number = Math.Max(page ?? 1, 1);
        var rows = await requests
            .OrderBy(r => r.State == RequestState.New ? 0 : r.State == RequestState.InWork ? 1 : 2)
            .ThenByDescending(r => r.Urgency)
            .ThenByDescending(r => r.UpdatedAt)
            .Skip((number - 1) * size).Take(size).ToListAsync(ct);
        return new Page<RequestListItem>(await ItemsAsync(db, rows, me, ct), total);
    }

    /// <summary>Заявки текущего пользователя с изменениями, которых он не видел: для «Мои заявки» и панели Revit.</summary>
    private static async Task<List<RequestListItem>> UpdatesAsync(ToadFlowDb db, ClaimsPrincipal user, CancellationToken ct)
    {
        var me = user.UserId();
        var rows = await db.Set<FamilyRequest>().AsNoTracking()
            .Where(r => db.Set<FamilyRequestMember>().Any(m => m.RequestId == r.Id && m.UserId == me && (m.SeenAt == null ? r.UpdatedAt > m.JoinedAt : r.UpdatedAt > m.SeenAt)))
            .OrderByDescending(r => r.UpdatedAt).Take(50).ToListAsync(ct);
        return await ItemsAsync(db, rows, me, ct);
    }

    /// <summary>Пока проектировщик пишет заявку: похожие семейства каталога и похожие открытые заявки.</summary>
    private static async Task<SimilarResult> SimilarAsync(ToadFlowDb db, ClaimsPrincipal user, string? q, CancellationToken ct)
    {
        var words = Words(q).Where(w => w.Length >= 3).ToList();
        if (words.Count == 0) return new SimilarResult([], []);

        var cards = await db.Set<FamilyCard>().AsNoTracking().Visible(user, null, false)
            .Where(AnyWord<FamilyCard>(words, nameof(FamilyCard.SearchText))).Take(50).ToListAsync(ct);
        var previews = await db.Set<FamilyVersion>().AsNoTracking().Where(v => cards.Select(c => c.CurrentVersionId).Contains(v.Id) && v.HasPreview)
            .ToDictionaryAsync(v => v.Id, v => v.Hash, ct);
        var families = cards.OrderByDescending(c => words.Count(w => c.SearchText.Contains(w))).ThenByDescending(c => c.LoadCount).Take(SimilarCount)
            .Select(c => new FamilyHit(c.Id, c.Name, c.Section, c.RelativePath, c.Category, c.RevitYear, c.TypeCount, c.State,
                c.CurrentVersionId is { } v && previews.ContainsKey(v), c.UpdatedAt, c.CurrentVersionId is { } p ? previews.GetValueOrDefault(p) : null, c.LoadCount))
            .ToList();

        var open = await db.Set<FamilyRequest>().AsNoTracking().Where(r => r.State == RequestState.New || r.State == RequestState.InWork)
            .Where(AnyWord<FamilyRequest>(words, nameof(FamilyRequest.SearchText))).Take(50).ToListAsync(ct);
        var similar = open.OrderByDescending(r => words.Count(w => r.SearchText.Contains(w))).ThenByDescending(r => r.UpdatedAt).Take(SimilarCount).ToList();
        return new SimilarResult(families, await ItemsAsync(db, similar, user.UserId(), ct));
    }

    private static async Task<List<RequestAssignee>> AssigneesAsync(ToadFlowDb db, CancellationToken ct) =>
        await db.Users.AsNoTracking().Where(u => !u.IsDisabled && !u.IsPending && (u.Role == UserRole.Operator || u.Role == UserRole.Admin))
            .OrderBy(u => u.DisplayName).Select(u => new RequestAssignee(u.Id, u.DisplayName)).ToListAsync(ct);

    private static async Task<IResult> GetAsync(Guid id, ToadFlowDb db, ClaimsPrincipal user, CancellationToken ct)
    {
        var request = await db.Set<FamilyRequest>().AsNoTracking().SingleOrDefaultAsync(r => r.Id == id, ct);
        if (request is null) return Results.NotFound();

        var me = user.UserId();
        var members = await db.Set<FamilyRequestMember>().AsNoTracking().Where(m => m.RequestId == id).OrderBy(m => m.JoinedAt).ToListAsync(ct);
        var messages = await db.Set<FamilyRequestMessage>().AsNoTracking().Where(m => m.RequestId == id).OrderBy(m => m.At).ThenBy(m => m.Id).ToListAsync(ct);
        var files = await db.Set<FamilyRequestFile>().AsNoTracking().Where(f => f.RequestId == id).OrderBy(f => f.At).ToListAsync(ct);
        var names = await NamesAsync(db, members.Select(m => m.UserId).Concat(messages.Select(m => m.AuthorUserId).OfType<Guid>()).Concat(files.Select(f => f.UploadedBy)), ct);
        var item = (await ItemsAsync(db, [request], me, ct))[0];
        return Results.Ok(new RequestView(item, request.Text, request.Reason,
            members.Select(m => new RequestMemberView(m.UserId, names.GetValueOrDefault(m.UserId, "?"))).ToList(),
            messages.Select(m => new RequestMessageView(m.Id, m.AuthorUserId, m.AuthorUserId is { } a ? names.GetValueOrDefault(a, "?") : null, m.Text, m.IsEvent, m.At))
                .ToList(),
            files.Select(f => new RequestFileView(f.Id, f.FileName, f.Size, f.ContentType, names.GetValueOrDefault(f.UploadedBy, "?"), f.At)).ToList()));
    }

    private static async Task<IResult> FileAsync(Guid id, Guid fileId, ToadFlowDb db, IOptions<FamiliesStorageOptions> storage, CancellationToken ct)
    {
        var file = await db.Set<FamilyRequestFile>().AsNoTracking().SingleOrDefaultAsync(f => f.Id == fileId && f.RequestId == id, ct);
        var path = file is null ? null : FilePath(storage.Value.Root, id, fileId);
        if (file is null || !File.Exists(path)) return Results.NotFound();
        return Results.File(path, "application/octet-stream", file.FileName);
    }

    private static async Task<IResult> SeenAsync(Guid id, ToadFlowDb db, ClaimsPrincipal user, TimeProvider time, CancellationToken ct)
    {
        var me = user.UserId();
        await db.Set<FamilyRequestMember>().Where(m => m.RequestId == id && m.UserId == me).ExecuteUpdateAsync(m => m.SetProperty(x => x.SeenAt, time.GetUtcNow()), ct);
        return Results.Ok();
    }

    private static async Task<IResult> CreateAsync(NewRequest body, ToadFlowDb db, ClaimsPrincipal user, TimeProvider time, IDomainEvents events, CancellationToken ct)
    {
        var title = body.Title?.Trim() ?? "";
        var text = body.Text?.Trim() ?? "";
        var section = body.Section?.Trim() ?? "";
        var project = body.Project?.Trim() ?? "";
        if (title.Length == 0) return Results.BadRequest(new { error = "Напишите, какое семейство нужно" });
        if (title.Length > MaxTitle || text.Length > MaxText || section.Length > 100 || project.Length > 200)
            return Results.BadRequest(new { error = $"Что нужно — до {MaxTitle} знаков, подробности — до {MaxText}, раздел — до 100, проект — до 200" });
        if (!Enum.IsDefined(body.Urgency)) return Results.BadRequest(new { error = "Срочность неизвестна" });

        var now = time.GetUtcNow();
        var me = user.UserId();
        var request = new FamilyRequest
        {
            Id = Guid.CreateVersion7(),
            Title = title,
            Text = text,
            Section = section,
            Urgency = body.Urgency,
            Project = project,
            State = RequestState.New,
            AuthorUserId = me,
            CreatedAt = now,
            UpdatedAt = now,
            SearchText = Search(title, text, section, project)
        };
        db.Set<FamilyRequest>().Add(request);
        db.Set<FamilyRequestMember>().Add(new FamilyRequestMember { RequestId = request.Id, UserId = me, JoinedAt = now, SeenAt = now });
        await db.SaveChangesAsync(ct);
        events.Publish(new CatalogChanged(user.Actor(), Requests.UiEntity, request.Id, $"Семейства: новая заявка № {request.Number} «{title}»"));
        return Results.Ok(new { request.Id, request.Number });
    }

    private static Task<IResult> JoinAsync(Guid id, ToadFlowDb db, ClaimsPrincipal user, TimeProvider time, IDomainEvents events, CancellationToken ct) =>
        ChangeAsync(id, db, user, time, events, ct, async (request, me, now) =>
        {
            if (!request.IsOpen) return "Заявка уже закрыта";
            if (await db.Set<FamilyRequestMember>().AnyAsync(m => m.RequestId == id && m.UserId == me, ct)) return null;
            db.Set<FamilyRequestMember>().Add(new FamilyRequestMember { RequestId = id, UserId = me, JoinedAt = now, SeenAt = now });
            return null;
        }, "присоединился к заявке", touch: false);

    private static Task<IResult> LeaveAsync(Guid id, ToadFlowDb db, ClaimsPrincipal user, TimeProvider time, IDomainEvents events, CancellationToken ct) =>
        ChangeAsync(id, db, user, time, events, ct, async (request, me, _) =>
        {
            if (request.AuthorUserId == me) return "Автор не может выйти из своей заявки";
            await db.Set<FamilyRequestMember>().Where(m => m.RequestId == id && m.UserId == me).ExecuteDeleteAsync(ct);
            return null;
        }, null, touch: false);

    private static async Task<IResult> MessageAsync(Guid id, RequestMessageRequest body, ToadFlowDb db, ClaimsPrincipal user, TimeProvider time, IDomainEvents events,
        CancellationToken ct)
    {
        var text = body.Text?.Trim() ?? "";
        if (text.Length == 0 || text.Length > MaxText) return Results.BadRequest(new { error = $"Сообщение — от 1 до {MaxText} знаков" });
        return await ChangeAsync(id, db, user, time, events, ct, async (request, me, now) =>
        {
            if (!await CanWriteAsync(db, user, request, ct)) return "Писать в заявку могут её участники и координаторы — присоединитесь к ней";
            db.Set<FamilyRequestMessage>().Add(new FamilyRequestMessage { Id = Guid.CreateVersion7(), RequestId = id, AuthorUserId = me, Text = text, At = now });
            return null;
        }, null);
    }

    private static async Task<IResult> UploadAsync(Guid id, HttpRequest http, ToadFlowDb db, ClaimsPrincipal user, TimeProvider time, IDomainEvents events,
        IOptions<FamiliesStorageOptions> storage, CancellationToken ct)
    {
        if (!http.HasFormContentType) return Results.BadRequest(new { error = "Нужен файл" });
        var form = await http.ReadFormAsync(ct);
        if (form.Files.Count != 1) return Results.BadRequest(new { error = "Один файл за раз" });
        var upload = form.Files[0];
        if (upload.Length == 0 || upload.Length > MaxFileSize) return Results.BadRequest(new { error = $"Файл — до {MaxFileSize / 1024 / 1024} МБ" });
        var name = Path.GetFileName(upload.FileName.Replace('\\', '/')).Trim();
        if (name.Length == 0 || name.Length > 200) return Results.BadRequest(new { error = "Имя файла — до 200 знаков" });

        var fileId = Guid.CreateVersion7();
        return await ChangeAsync(id, db, user, time, events, ct, async (request, me, now) =>
        {
            if (!await CanWriteAsync(db, user, request, ct)) return "Прикладывать файлы могут участники заявки и координаторы — присоединитесь к ней";
            if (await db.Set<FamilyRequestFile>().CountAsync(f => f.RequestId == id, ct) >= MaxFiles) return $"У заявки уже {MaxFiles} вложений";

            var path = FilePath(storage.Value.Root, id, fileId);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await using (var target = File.Create(path))
            await using (var source = upload.OpenReadStream())
                await source.CopyToAsync(target, ct);

            db.Set<FamilyRequestFile>().Add(new FamilyRequestFile
            {
                Id = fileId,
                RequestId = id,
                FileName = name,
                ContentType = upload.ContentType is { Length: > 0 and <= 200 } type ? type : "application/octet-stream",
                Size = upload.Length,
                UploadedBy = me,
                At = now
            });
            return null;
        }, $"приложил файл «{name}»");
    }

    private static Task<IResult> TakeAsync(Guid id, ToadFlowDb db, ClaimsPrincipal user, TimeProvider time, IDomainEvents events, CancellationToken ct) =>
        ChangeAsync(id, db, user, time, events, ct, (request, me, _) =>
        {
            if (!request.IsOpen) return Task.FromResult<string?>("Заявка закрыта — сначала верните её в работу");
            request.AssigneeUserId = me;
            request.State = RequestState.InWork;
            return Task.FromResult<string?>(null);
        }, "взял в работу");

    private static async Task<IResult> AssignAsync(Guid id, RequestAssign body, ToadFlowDb db, ClaimsPrincipal user, TimeProvider time, IDomainEvents events,
        CancellationToken ct)
    {
        var assignee = await db.Users.AsNoTracking().SingleOrDefaultAsync(u => u.Id == body.UserId && !u.IsDisabled, ct);
        if (assignee is null || assignee.Role is not (UserRole.Operator or UserRole.Admin))
            return Results.BadRequest(new { error = "Передать можно координатору или администратору" });
        return await ChangeAsync(id, db, user, time, events, ct, (request, _, _) =>
        {
            if (!request.IsOpen) return Task.FromResult<string?>("Заявка закрыта — сначала верните её в работу");
            request.AssigneeUserId = assignee.Id;
            request.State = RequestState.InWork;
            return Task.FromResult<string?>(null);
        }, $"передал заявку: {assignee.DisplayName}");
    }

    private static Task<IResult> DoneAsync(Guid id, RequestCard body, ToadFlowDb db, ClaimsPrincipal user, TimeProvider time, IDomainEvents events, CancellationToken ct) =>
        CloseWithCardAsync(id, body.CardId, RequestState.Done, "готова", db, user, time, events, ct);

    private static Task<IResult> ExistsAsync(Guid id, RequestCard body, ToadFlowDb db, ClaimsPrincipal user, TimeProvider time, IDomainEvents events, CancellationToken ct) =>
        CloseWithCardAsync(id, body.CardId, RequestState.Exists, "такое семейство уже есть", db, user, time, events, ct);

    private static async Task<IResult> CloseWithCardAsync(Guid id, Guid cardId, RequestState state, string what, ToadFlowDb db, ClaimsPrincipal user,
        TimeProvider time, IDomainEvents events, CancellationToken ct)
    {
        var card = await db.Set<FamilyCard>().AsNoTracking().SingleOrDefaultAsync(c => c.Id == cardId && c.State != CardState.Archived, ct);
        if (card is null) return Results.BadRequest(new { error = "Семейство не найдено в каталоге" });
        return await ChangeAsync(id, db, user, time, events, ct, (request, me, now) =>
        {
            request.State = state;
            request.CardId = card.Id;
            request.AssigneeUserId ??= me;
            request.Reason = null;
            request.ClosedAt = now;
            return Task.FromResult<string?>(null);
        }, $"{what}: «{card.Name}»");
    }

    private static async Task<IResult> RejectAsync(Guid id, RequestReject body, ToadFlowDb db, ClaimsPrincipal user, TimeProvider time, IDomainEvents events,
        CancellationToken ct)
    {
        var reason = body.Reason?.Trim() ?? "";
        if (reason.Length == 0 || reason.Length > MaxText) return Results.BadRequest(new { error = "Напишите причину отклонения" });
        return await ChangeAsync(id, db, user, time, events, ct, (request, me, now) =>
        {
            request.State = RequestState.Rejected;
            request.Reason = reason;
            request.CardId = null;
            request.AssigneeUserId ??= me;
            request.ClosedAt = now;
            return Task.FromResult<string?>(null);
        }, $"отклонил: {reason}");
    }

    private static Task<IResult> ReopenAsync(Guid id, ToadFlowDb db, ClaimsPrincipal user, TimeProvider time, IDomainEvents events, CancellationToken ct) =>
        ChangeAsync(id, db, user, time, events, ct, (request, _, _) =>
        {
            if (request.IsOpen) return Task.FromResult<string?>(null);
            request.State = request.AssigneeUserId is null ? RequestState.New : RequestState.InWork;
            request.ClosedAt = null;
            request.Reason = null;
            return Task.FromResult<string?>(null);
        }, "вернул в работу");

    /// <summary>
    ///     Изменение заявки одним сохранением: действие возвращает отказ текстом или null. <paramref name="eventText" /> — строка в переписку
    ///     («Иванов: взял в работу»). Тот, кто изменил, своё изменение уже видел.
    /// </summary>
    private static async Task<IResult> ChangeAsync(Guid id, ToadFlowDb db, ClaimsPrincipal user, TimeProvider time, IDomainEvents events, CancellationToken ct,
        Func<FamilyRequest, Guid, DateTimeOffset, Task<string?>> change, string? eventText, bool touch = true)
    {
        var request = await db.Set<FamilyRequest>().SingleOrDefaultAsync(r => r.Id == id, ct);
        if (request is null) return Results.NotFound();

        var me = user.UserId();
        var now = time.GetUtcNow();
        if (await change(request, me, now) is { } refusal) return Results.BadRequest(new { error = refusal });

        if (touch) request.UpdatedAt = now;
        if (eventText is not null)
            db.Set<FamilyRequestMessage>().Add(new FamilyRequestMessage { Id = Guid.CreateVersion7(), RequestId = id, AuthorUserId = me, Text = eventText, IsEvent = true, At = now });
        await db.SaveChangesAsync(ct);
        await db.Set<FamilyRequestMember>().Where(m => m.RequestId == id && m.UserId == me).ExecuteUpdateAsync(m => m.SetProperty(x => x.SeenAt, now), ct);
        events.Publish(new CatalogChanged(user.Actor(), Requests.UiEntity, id, $"Семейства: заявка № {request.Number} — {eventText ?? "сообщение"}"));
        return Results.Ok();
    }

    private static async Task<bool> CanWriteAsync(ToadFlowDb db, ClaimsPrincipal user, FamilyRequest request, CancellationToken ct)
    {
        if (user.IsStaff()) return true;
        var me = user.UserId();
        return await db.Set<FamilyRequestMember>().AnyAsync(m => m.RequestId == request.Id && m.UserId == me, ct);
    }

    private static async Task<List<RequestListItem>> ItemsAsync(ToadFlowDb db, IReadOnlyList<FamilyRequest> rows, Guid me, CancellationToken ct)
    {
        if (rows.Count == 0) return [];
        var ids = rows.Select(r => r.Id).ToList();
        var members = await db.Set<FamilyRequestMember>().AsNoTracking().Where(m => ids.Contains(m.RequestId)).ToListAsync(ct);
        var cardIds = rows.Select(r => r.CardId).OfType<Guid>().Distinct().ToList();
        var cards = await db.Set<FamilyCard>().AsNoTracking().Where(c => cardIds.Contains(c.Id)).ToDictionaryAsync(c => c.Id, c => c.Name, ct);
        var names = await NamesAsync(db, rows.Select(r => r.AuthorUserId).Concat(rows.Select(r => r.AssigneeUserId).OfType<Guid>()), ct);
        return rows.Select(r =>
        {
            var mine = members.FirstOrDefault(m => m.RequestId == r.Id && m.UserId == me);
            var unread = mine is not null && r.UpdatedAt > (mine.SeenAt ?? mine.JoinedAt);
            return new RequestListItem(r.Id, r.Number, r.Title, r.Section, r.Urgency, r.Project, r.State, r.AuthorUserId, names.GetValueOrDefault(r.AuthorUserId, "?"),
                r.AssigneeUserId, r.AssigneeUserId is { } a ? names.GetValueOrDefault(a, "?") : null, r.CardId, r.CardId is { } c ? cards.GetValueOrDefault(c) : null,
                members.Count(m => m.RequestId == r.Id), mine is not null, unread, r.CreatedAt, r.UpdatedAt);
        }).ToList();
    }

    private static async Task<Dictionary<Guid, string>> NamesAsync(ToadFlowDb db, IEnumerable<Guid> ids, CancellationToken ct)
    {
        var list = ids.Distinct().ToList();
        return await db.Users.AsNoTracking().Where(u => list.Contains(u.Id)).ToDictionaryAsync(u => u.Id, u => u.DisplayName, ct);
    }

    public static string FilePath(string root, Guid requestId, Guid fileId) => Path.Combine(root, "requests", requestId.ToString("N"), fileId.ToString("N"));

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
