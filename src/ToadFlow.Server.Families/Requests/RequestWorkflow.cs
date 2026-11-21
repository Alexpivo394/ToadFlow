using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using ToadFlow.Contracts;
using ToadFlow.Server.Auth;
using ToadFlow.Server.Data;
using ToadFlow.Server.Events;
using ToadFlow.Server.Families.Data;

namespace ToadFlow.Server.Families.Requests;

/// <summary>Подача, правка и переходы заявки.</summary>
internal static partial class RequestEndpoints
{
    private const int MinRevitYear = 2015;
    private const int MaxRevitYear = 2040;

    private static async Task<IResult> CreateAsync(NewRequest body, ToadFlowDb db, ClaimsPrincipal user, TimeProvider time, IDomainEvents events,
        IOptions<FamiliesStorageOptions> storage, CancellationToken ct)
    {
        var title = body.Title?.Trim() ?? "";
        var text = body.Text?.Trim() ?? "";
        var section = body.Section?.Trim() ?? "";
        var project = body.Project?.Trim() ?? "";
        if (title.Length == 0) return Results.BadRequest(new { error = "Напишите, какое семейство нужно" });
        if (Fields(title, text, section, project, body.RevitYear) is { } invalid) return Results.BadRequest(new { error = invalid });
        if (body.Urgency is { } urgency && !Enum.IsDefined(urgency)) return Results.BadRequest(new { error = "Срочность неизвестна" });
        var kind = body.Kind ?? RequestKind.New;
        if (!Enum.IsDefined(kind)) return Results.BadRequest(new { error = "Вид заявки неизвестен" });

        Guid? target = null;
        if (kind == RequestKind.Fix)
        {
            var card = body.TargetCardId is { } cardId
                ? await db.Set<FamilyCard>().AsNoTracking().SingleOrDefaultAsync(c => c.Id == cardId && c.State != CardState.Archived, ct)
                : null;
            if (card is null) return Results.BadRequest(new { error = "Укажите семейство каталога, в котором проблема" });
            target = card.Id;
            if (section.Length == 0) section = card.Section;
        }

        var me = user.UserId();
        Guid? direction;
        if (body.DirectionId is { } chosen)
        {
            if (!await db.Set<FamilyDirection>().AnyAsync(d => d.Id == chosen, ct)) return Results.BadRequest(new { error = "Направление не найдено" });
            direction = chosen;
        }
        else direction = await Directions.ResolveAsync(db, section, me, ct);

        var now = time.GetUtcNow();
        var request = new FamilyRequest
        {
            Id = Guid.CreateVersion7(),
            Kind = kind,
            Title = title,
            Text = text,
            Section = section,
            BlocksIssue = body.BlocksIssue ?? body.Urgency == RequestUrgency.Urgent,
            NeededBy = body.NeededBy,
            RevitYear = body.RevitYear,
            Project = project,
            State = RequestState.New,
            DirectionId = direction,
            TargetCardId = target,
            AuthorUserId = me,
            CreatedAt = now,
            UpdatedAt = now,
            SearchText = Search(title, text, section, project)
        };
        db.Set<FamilyRequest>().Add(request);
        db.Set<FamilyRequestMember>().Add(new FamilyRequestMember { RequestId = request.Id, UserId = me, JoinedAt = now, SeenAt = now });
        if (await AttachAsync(db, storage.Value.Root, request.Id, null, body.FileIds, me, ct) is { } fileRefusal) return Results.BadRequest(new { error = fileRefusal });
        await db.SaveChangesAsync(ct);
        events.Publish(new CatalogChanged(user.Actor(), Requests.UiEntity, request.Id, $"Семейства: новая заявка № {request.Number} «{title}»"));
        return Results.Ok(new { request.Id, request.Number });
    }

    /// <summary>Отказ по длинам полей и версии Revit; null — всё в порядке.</summary>
    private static string? Fields(string title, string text, string section, string project, int? revitYear)
    {
        if (title.Length > MaxTitle || text.Length > MaxText || section.Length > MaxSection || project.Length > MaxProject)
            return $"Что нужно — до {MaxTitle} знаков, подробности — до {MaxText}, раздел — до {MaxSection}, проект — до {MaxProject}";
        if (revitYear is < MinRevitYear or > MaxRevitYear) return $"Версия Revit — от {MinRevitYear} до {MaxRevitYear}";
        return null;
    }

    /// <summary>Правка описания: автор — пока заявка открыта, координатор — всегда.</summary>
    private static async Task<IResult> EditAsync(Guid id, RequestEdit body, ToadFlowDb db, ClaimsPrincipal user, TimeProvider time, IDomainEvents events,
        CancellationToken ct)
    {
        var title = body.Title?.Trim() ?? "";
        var text = body.Text?.Trim() ?? "";
        var section = body.Section?.Trim() ?? "";
        var project = body.Project?.Trim() ?? "";
        if (title.Length == 0) return Results.BadRequest(new { error = "Напишите, какое семейство нужно" });
        if (Fields(title, text, section, project, body.RevitYear) is { } invalid) return Results.BadRequest(new { error = invalid });

        var request = await db.Set<FamilyRequest>().AsNoTracking().SingleOrDefaultAsync(r => r.Id == id, ct);
        if (request is null) return Results.NotFound();
        if (!user.IsStaff() && !(request.AuthorUserId == user.UserId() && request.IsOpen)) return Results.Forbid();

        return await ChangeAsync(id, db, user, time, events, ct, (r, _, _) =>
        {
            if (r.NeededBy != body.NeededBy) r.OverdueNotified = false;
            r.Title = title;
            r.Text = text;
            r.Section = section;
            r.Project = project;
            r.NeededBy = body.NeededBy;
            r.BlocksIssue = body.BlocksIssue;
            r.RevitYear = body.RevitYear;
            r.SearchText = Search(title, text, section, project);
            return Task.FromResult<string?>(null);
        }, "изменил описание");
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
        }, (string?)null, touch: false);

    private static async Task<IResult> MessageAsync(Guid id, RequestMessageRequest body, ToadFlowDb db, ClaimsPrincipal user, TimeProvider time, IDomainEvents events,
        IOptions<FamiliesStorageOptions> storage,
        CancellationToken ct)
    {
        var text = body.Text?.Trim() ?? "";
        if ((text.Length == 0 && body.FileIds is not { Length: > 0 }) || text.Length > MaxText)
            return Results.BadRequest(new { error = $"Сообщение — до {MaxText} знаков; пустое — только с файлами" });
        var answered = false;
        return await ChangeAsync(id, db, user, time, events, ct, async (request, me, now) =>
        {
            if (!await CanWriteAsync(db, user, request, ct)) return "Писать в заявку могут её участники и координаторы — присоединитесь к ней";
            if (await AddMessageAsync(db, storage.Value.Root, id, me, text, body.FileIds, now, ct) is { } refused) return refused;
            answered = await AnswerAsync(db, request, me, ct);
            return null;
        }, () => answered ? "автор ответил" : null);
    }

    /// <summary>Ответ автора или участника на вопрос исполнителя: заявка снова у исполнителя.</summary>
    private static async Task<bool> AnswerAsync(ToadFlowDb db, FamilyRequest request, Guid writer, CancellationToken ct)
    {
        if (request.State != RequestState.NeedInfo || writer == request.AssigneeUserId || !await IsMemberAsync(db, request.Id, writer, ct)) return false;
        request.State = RequestState.InWork;
        return true;
    }

    private static async Task<IResult> TakeAsync(Guid id, HttpRequest http, ToadFlowDb db, ClaimsPrincipal user, TimeProvider time, IDomainEvents events,
        CancellationToken ct)
    {
        var body = http.HasJsonContentType() ? await http.ReadFromJsonAsync<RequestTake>(ct) : null;
        return await ChangeAsync(id, db, user, time, events, ct, (request, me, _) =>
        {
            if (!request.IsOpen) return Task.FromResult<string?>("Заявка закрыта — сначала верните её в работу");
            request.AssigneeUserId = me;
            if (request.State is RequestState.New) request.State = RequestState.InWork;
            if (body?.PromisedBy is { } promised) request.PromisedBy = promised;
            return Task.FromResult<string?>(null);
        }, body?.PromisedBy is { } date ? $"взял в работу, обещает к {date:dd.MM.yyyy}" : "взял в работу");
    }

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
            if (request.State is RequestState.New) request.State = RequestState.InWork;
            if (body.PromisedBy is { } promised) request.PromisedBy = promised;
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
            if (request.State is RequestState.Rejected or RequestState.Withdrawn)
                return Task.FromResult<string?>("Заявка закрыта — сначала верните её в работу");
            ToReview(request, card.Id, state == RequestState.Exists ? RequestResolution.Existed : RequestResolution.Made, me, now);
            return Task.FromResult<string?>(null);
        }, $"{what}: «{card.Name}» — на приёмке у автора");
    }

    /// <summary>Сделано или найдено — на приёмку автору; от <paramref name="now" /> считается автоприёмка.</summary>
    internal static void ToReview(FamilyRequest request, Guid cardId, RequestResolution resolution, Guid? assignee, DateTimeOffset now)
    {
        request.State = RequestState.Review;
        request.CardId = cardId;
        request.Resolution = resolution;
        request.ReviewSince = now;
        request.ReminderSent = false;
        request.AssigneeUserId ??= assignee;
        request.Reason = null;
        request.ClosedAt = null;
    }

    /// <summary>Приёмка: <see cref="RequestResolution.Made" /> — «готова», <see cref="RequestResolution.Existed" /> — «уже есть».</summary>
    internal static void Accept(FamilyRequest request, DateTimeOffset now)
    {
        request.State = request.Resolution == RequestResolution.Existed ? RequestState.Exists : RequestState.Done;
        request.ClosedAt = now;
    }

    private static async Task<IResult> PromiseAsync(Guid id, RequestTake body, ToadFlowDb db, ClaimsPrincipal user, TimeProvider time, IDomainEvents events,
        CancellationToken ct) =>
        await ChangeAsync(id, db, user, time, events, ct, (request, _, _) =>
        {
            if (!request.IsOpen) return Task.FromResult<string?>("Заявка закрыта");
            request.PromisedBy = body.PromisedBy;
            return Task.FromResult<string?>(null);
        }, body.PromisedBy is { } date ? $"обещает к {date:dd.MM.yyyy}" : "снял обещанный срок");

    /// <summary>Вопрос автору: сообщение и ход переходит к автору («ждёт автора»).</summary>
    private static async Task<IResult> AskAsync(Guid id, RequestMessageRequest body, ToadFlowDb db, ClaimsPrincipal user, TimeProvider time, IDomainEvents events,
        IOptions<FamiliesStorageOptions> storage,
        CancellationToken ct)
    {
        var text = body.Text?.Trim() ?? "";
        if (text.Length == 0 || text.Length > MaxText) return Results.BadRequest(new { error = $"Напишите вопрос — до {MaxText} знаков" });
        return await ChangeAsync(id, db, user, time, events, ct, async (request, me, now) =>
        {
            if (request.State is not (RequestState.New or RequestState.InWork or RequestState.NeedInfo)) return "Спросить можно, пока заявка в работе";
            request.State = RequestState.NeedInfo;
            request.AssigneeUserId ??= me;
            return await AddMessageAsync(db, storage.Value.Root, id, me, text, body.FileIds, now, ct);
        }, "ждёт ответа автора");
    }

    /// <summary>Сообщение человека с вложениями (свои «ничейные» загрузки).</summary>
    /// <returns>Отказ для показа; null — добавлено.</returns>
    private static async Task<string?> AddMessageAsync(ToadFlowDb db, string root, Guid requestId, Guid me, string text, Guid[]? fileIds, DateTimeOffset now,
        CancellationToken ct)
    {
        var message = new FamilyRequestMessage { Id = Guid.CreateVersion7(), RequestId = requestId, AuthorUserId = me, Text = text, At = now };
        db.Set<FamilyRequestMessage>().Add(message);
        return await AttachAsync(db, root, requestId, message.Id, fileIds, me, ct);
    }

    /// <summary>«Подходит» — автор или присоединившийся принимает работу.</summary>
    private static Task<IResult> AcceptAsync(Guid id, ToadFlowDb db, ClaimsPrincipal user, TimeProvider time, IDomainEvents events, CancellationToken ct) =>
        ChangeAsync(id, db, user, time, events, ct, async (request, me, now) =>
        {
            if (request.State != RequestState.Review) return "Заявка не на приёмке";
            if (!await IsMemberAsync(db, id, me, ct)) return "Принять может автор заявки или присоединившийся";
            Accept(request, now);
            return null;
        }, "принял: подходит");

    /// <summary>«Не то» — с объяснением; заявка возвращается тому же исполнителю.</summary>
    private static async Task<IResult> DeclineAsync(Guid id, RequestMessageRequest body, ToadFlowDb db, ClaimsPrincipal user, TimeProvider time,
        IOptions<FamiliesStorageOptions> storage,
        IDomainEvents events, CancellationToken ct)
    {
        var text = body.Text?.Trim() ?? "";
        if (text.Length == 0 || text.Length > MaxText) return Results.BadRequest(new { error = "Напишите, что не так" });
        return await ChangeAsync(id, db, user, time, events, ct, async (request, me, now) =>
        {
            if (request.State != RequestState.Review) return "Заявка не на приёмке";
            if (!await IsMemberAsync(db, id, me, ct)) return "Вернуть может автор заявки или присоединившийся";
            request.State = request.AssigneeUserId is null ? RequestState.New : RequestState.InWork;
            request.Resolution = null;
            request.ReviewSince = null;
            request.DeclineCount++;
            if (await AddMessageAsync(db, storage.Value.Root, id, me, text, body.FileIds, now, ct) is { } refused) return refused;
            return null;
        }, "не то — вернул в работу");
    }

    private static Task<IResult> WithdrawAsync(Guid id, ToadFlowDb db, ClaimsPrincipal user, TimeProvider time, IDomainEvents events, CancellationToken ct) =>
        ChangeAsync(id, db, user, time, events, ct, (request, me, now) =>
        {
            if (request.AuthorUserId != me) return Task.FromResult<string?>("Отозвать может только автор");
            if (!request.IsOpen) return Task.FromResult<string?>("Заявка уже закрыта");
            request.State = RequestState.Withdrawn;
            request.ClosedAt = now;
            return Task.FromResult<string?>(null);
        }, "отозвал заявку");

    private static async Task<IResult> DirectionAsync(Guid id, RequestDirection body, ToadFlowDb db, ClaimsPrincipal user, TimeProvider time, IDomainEvents events,
        CancellationToken ct)
    {
        string? name = null;
        if (body.DirectionId is { } directionId)
        {
            name = await db.Set<FamilyDirection>().Where(d => d.Id == directionId).Select(d => d.Name).SingleOrDefaultAsync(ct);
            if (name is null) return Results.BadRequest(new { error = "Направление не найдено" });
        }

        return await ChangeAsync(id, db, user, time, events, ct, (request, _, _) =>
        {
            request.DirectionId = body.DirectionId;
            return Task.FromResult<string?>(null);
        }, name is null ? "передал в общую очередь" : $"передал в направление «{name}»");
    }

    private static Task<bool> IsMemberAsync(ToadFlowDb db, Guid id, Guid user, CancellationToken ct) =>
        db.Set<FamilyRequestMember>().AnyAsync(m => m.RequestId == id && m.UserId == user, ct);

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
            request.Resolution = null;
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
            request.ReviewSince = null;
            request.Resolution = null;
            return Task.FromResult<string?>(null);
        }, "вернул в работу");

    /// <summary>
    ///     Изменение заявки одним сохранением: действие возвращает отказ текстом или null. <paramref name="eventText" /> — строка в переписку
    ///     («Иванов: взял в работу»). Тот, кто изменил, своё изменение уже видел.
    /// </summary>
    private static Task<IResult> ChangeAsync(Guid id, ToadFlowDb db, ClaimsPrincipal user, TimeProvider time, IDomainEvents events, CancellationToken ct,
        Func<FamilyRequest, Guid, DateTimeOffset, Task<string?>> change, string? eventText, bool touch = true) =>
        ChangeAsync(id, db, user, time, events, ct, change, () => eventText, touch);

    /// <param name="eventText">Строка о событии — вычисляется после изменения (например, «автор ответил» только если ответ сменил состояние).</param>
    private static async Task<IResult> ChangeAsync(Guid id, ToadFlowDb db, ClaimsPrincipal user, TimeProvider time, IDomainEvents events, CancellationToken ct,
        Func<FamilyRequest, Guid, DateTimeOffset, Task<string?>> change, Func<string?> eventText, bool touch = true)
    {
        var request = await db.Set<FamilyRequest>().SingleOrDefaultAsync(r => r.Id == id, ct);
        if (request is null) return Results.NotFound();

        var me = user.UserId();
        var now = time.GetUtcNow();
        if (await change(request, me, now) is { } refusal) return Results.BadRequest(new { error = refusal });
        var text = eventText();

        if (touch) request.UpdatedAt = now;
        if (text is not null)
            db.Set<FamilyRequestMessage>().Add(new FamilyRequestMessage { Id = Guid.CreateVersion7(), RequestId = id, AuthorUserId = me, Text = text, IsEvent = true, At = now });
        await db.SaveChangesAsync(ct);
        await db.Set<FamilyRequestMember>().Where(m => m.RequestId == id && m.UserId == me).ExecuteUpdateAsync(m => m.SetProperty(x => x.SeenAt, now), ct);
        events.Publish(new CatalogChanged(user.Actor(), Requests.UiEntity, id, $"Семейства: заявка № {request.Number} — {text ?? "сообщение"}"));
        return Results.Ok();
    }

    private static async Task<bool> CanWriteAsync(ToadFlowDb db, ClaimsPrincipal user, FamilyRequest request, CancellationToken ct)
    {
        if (user.IsStaff()) return true;
        var me = user.UserId();
        return await db.Set<FamilyRequestMember>().AnyAsync(m => m.RequestId == request.Id && m.UserId == me, ct);
    }
}
