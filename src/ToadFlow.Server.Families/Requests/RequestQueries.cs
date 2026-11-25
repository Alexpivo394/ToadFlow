using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using ToadFlow.Contracts;
using ToadFlow.Server.Auth;
using ToadFlow.Server.Data;
using ToadFlow.Server.Families.Data;
using ToadFlow.Server.Families.Search;
using ToadFlow.Server.Runs;
using ToadFlow.Server.Settings;

namespace ToadFlow.Server.Families.Requests;

/// <summary>Списки, очередь, просмотр заявки и подсказки формы.</summary>
internal static partial class RequestEndpoints
{
    /// <summary>
    ///     Заявки. <c>scope</c>: <c>mine</c> (по умолчанию) — где я участник или исполнитель; <c>direction</c> — открытые в моих направлениях
    ///     и общей очереди; <c>assigned</c> — открытые на мне; <c>unassigned</c> — открытые без исполнителя; <c>needinfo</c>, <c>review</c> —
    ///     ждут автора, на приёмке; <c>all</c> — все. <c>state</c> — состояния через запятую. Порядок: открытые выше; среди них блокирующие
    ///     выдачу, затем по сроку (просроченные первыми), затем у кого больше ждущих, затем старшие; закрытые — свежие выше.
    /// </summary>
    private static async Task<Page<RequestListItem>> ListAsync(ToadFlowDb db, ClaimsPrincipal user, ISettingsStore settings, TimeProvider time, string? scope,
        string? state, string? q, int? page, int? pageSize, CancellationToken ct)
    {
        var me = user.UserId();
        var requests = await ScopeAsync(db, db.Set<FamilyRequest>().AsNoTracking(), scope, me, ct);
        var states = (state ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(s => Enum.TryParse<RequestState>(s, true, out var parsed) ? parsed : (RequestState?)null).OfType<RequestState>().ToList();
        if (states.Count > 0) requests = requests.Where(r => states.Contains(r.State));
        foreach (var word in Words(q)) requests = requests.Where(r => EF.Functions.Like(r.SearchText, "%" + Escape(word) + "%", @"\"));

        var total = await requests.CountAsync(ct);
        var size = Math.Clamp(pageSize ?? 50, 1, 200);
        var number = Math.Max(page ?? 1, 1);
        var open = FamilyRequest.OpenStates;
        var members = db.Set<FamilyRequestMember>();
        var rows = await requests
            .OrderBy(r => open.Contains(r.State) ? 0 : 1)
            .ThenByDescending(r => open.Contains(r.State) && r.BlocksIssue)
            .ThenBy(r => open.Contains(r.State) && r.NeededBy != null ? 0 : 1)
            .ThenBy(r => open.Contains(r.State) ? r.NeededBy : null)
            .ThenByDescending(r => open.Contains(r.State) ? members.Count(m => m.RequestId == r.Id) : 0)
            .ThenBy(r => open.Contains(r.State) ? r.CreatedAt : (DateTimeOffset?)null)
            .ThenByDescending(r => r.UpdatedAt)
            .Skip((number - 1) * size).Take(size).ToListAsync(ct);
        return new Page<RequestListItem>(await ItemsAsync(db, rows, me, await TodayAsync(settings, time, ct), time.GetUtcNow(), ct), total);
    }

    private static async Task<IQueryable<FamilyRequest>> ScopeAsync(ToadFlowDb db, IQueryable<FamilyRequest> requests, string? scope, Guid me, CancellationToken ct)
    {
        var open = FamilyRequest.OpenStates;
        var members = db.Set<FamilyRequestMember>();
        return scope switch
        {
            "all" => requests,
            "direction" => await InMyDirectionsAsync(db, requests.Where(r => open.Contains(r.State)), me, ct),
            "assigned" => requests.Where(r => open.Contains(r.State) && r.AssigneeUserId == me),
            "unassigned" => requests.Where(r => open.Contains(r.State) && r.AssigneeUserId == null),
            "needinfo" => requests.Where(r => r.State == RequestState.NeedInfo),
            "review" => requests.Where(r => r.State == RequestState.Review),
            _ => requests.Where(r => members.Any(m => m.RequestId == r.Id && m.UserId == me) || r.AssigneeUserId == me)
        };
    }

    /// <summary>
    ///     Мои направления (где я координатор) и общая очередь: без направления или в направлении без координаторов. Не координатор ни
    ///     одного направления видит всё.
    /// </summary>
    internal static async Task<IQueryable<FamilyRequest>> InMyDirectionsAsync(ToadFlowDb db, IQueryable<FamilyRequest> requests, Guid me, CancellationToken ct)
    {
        var directions = await db.Set<FamilyDirection>().AsNoTracking().Select(d => new { d.Id, d.Coordinators }).ToListAsync(ct);
        var mine = directions.Where(d => d.Coordinators.Contains(me)).Select(d => d.Id).ToList();
        if (mine.Count == 0) return requests;
        var orphan = directions.Where(d => d.Coordinators.Length == 0).Select(d => d.Id).ToList();
        var visible = mine.Concat(orphan).ToList();
        return requests.Where(r => r.DirectionId == null || visible.Contains(r.DirectionId.Value));
    }

    /// <summary>Заявки текущего пользователя с изменениями, которых он не видел: для «Мои заявки» и панели Revit.</summary>
    private static async Task<List<RequestListItem>> UpdatesAsync(ToadFlowDb db, ClaimsPrincipal user, ISettingsStore settings, TimeProvider time, CancellationToken ct)
    {
        var me = user.UserId();
        var rows = await db.Set<FamilyRequest>().AsNoTracking()
            .Where(r => db.Set<FamilyRequestMember>().Any(m => m.RequestId == r.Id && m.UserId == me && (m.SeenAt == null ? r.UpdatedAt > m.JoinedAt : r.UpdatedAt > m.SeenAt)))
            .OrderByDescending(r => r.UpdatedAt).Take(50).ToListAsync(ct);
        return await ItemsAsync(db, rows, me, await TodayAsync(settings, time, ct), time.GetUtcNow(), ct);
    }

    /// <summary>Пока проектировщик пишет заявку: похожие семейства каталога и похожие открытые заявки.</summary>
    private static async Task<SimilarResult> SimilarAsync(ToadFlowDb db, ClaimsPrincipal user, ISettingsStore settings, TimeProvider time, string? q, CancellationToken ct)
    {
        var words = Words(q).Where(w => w.Length >= 3).ToList();
        if (words.Count == 0) return new SimilarResult([], []);

        var cards = await db.Set<FamilyCard>().AsNoTracking().Visible(user, null, false)
            .Where(AnyWord<FamilyCard>(words, nameof(FamilyCard.SearchText))).Take(50).ToListAsync(ct);
        var previews = await db.Set<FamilyVersion>().AsNoTracking().Where(v => cards.Select(c => c.CurrentVersionId).Contains(v.Id) && v.HasPreview)
            .ToDictionaryAsync(v => v.Id, v => v.Hash, ct);
        var families = cards.OrderByDescending(c => words.Count(w => c.SearchText.Contains(w))).ThenByDescending(c => c.LoadCount).Take(SimilarCount)
            .Select(c => new FamilyHit(c.Id, c.Name, c.Section, c.RelativePath, c.Category, c.RevitYear, c.TypeCount, c.State,
                c.CurrentVersionId is { } v && previews.ContainsKey(v), c.UpdatedAt, c.CurrentVersionId is { } p ? previews.GetValueOrDefault(p) : null, c.LoadCount,
                c.CoordinatorsOnly, 0))
            .ToList();

        var openStates = FamilyRequest.OpenStates;
        var open = await db.Set<FamilyRequest>().AsNoTracking().Where(r => openStates.Contains(r.State))
            .Where(AnyWord<FamilyRequest>(words, nameof(FamilyRequest.SearchText))).Take(50).ToListAsync(ct);
        var similar = open.OrderByDescending(r => words.Count(w => r.SearchText.Contains(w))).ThenByDescending(r => r.UpdatedAt).Take(SimilarCount).ToList();
        return new SimilarResult(families, await ItemsAsync(db, similar, user.UserId(), await TodayAsync(settings, time, ct), time.GetUtcNow(), ct));
    }

    /// <summary>Проекты для подсказки в форме: из заявок и загрузок семейств, частые выше; <c>q</c> — вхождение без учёта регистра.</summary>
    private static async Task<List<string>> ProjectsAsync(ToadFlowDb db, string? q, CancellationToken ct)
    {
        var fromRequests = db.Set<FamilyRequest>().AsNoTracking().Where(r => r.Project != "").Select(r => r.Project);
        var fromLoads = db.Set<FamilyLoad>().AsNoTracking().Where(l => l.Project != "").Select(l => l.Project);
        var all = fromRequests.Concat(fromLoads);
        if (!string.IsNullOrWhiteSpace(q)) all = all.Where(p => EF.Functions.ILike(p, "%" + Escape(q.Trim()) + "%", @"\"));
        return await all.GroupBy(p => p).OrderByDescending(g => g.Count()).ThenBy(g => g.Key).Select(g => g.Key).Take(20).ToListAsync(ct);
    }

    /// <summary>Цифры над очередью координатора.</summary>
    private static async Task<RequestStats> StatsAsync(ToadFlowDb db, ClaimsPrincipal user, ISettingsStore settings, TimeProvider time, CancellationToken ct)
    {
        var open = FamilyRequest.OpenStates;
        var today = await TodayAsync(settings, time, ct);
        var requests = db.Set<FamilyRequest>().AsNoTracking();
        var opened = requests.Where(r => open.Contains(r.State));
        var since = time.GetUtcNow().AddDays(-30);
        var closed = await requests.Where(r => (r.State == RequestState.Done || r.State == RequestState.Exists) && r.ClosedAt >= since)
            .Select(r => new { r.CreatedAt, r.ClosedAt }).ToListAsync(ct);
        var hours = closed.Select(c => (c.ClosedAt!.Value - c.CreatedAt).TotalHours).Order().ToList();
        double? median = hours.Count == 0 ? null : hours.Count % 2 == 1 ? hours[hours.Count / 2] : (hours[hours.Count / 2 - 1] + hours[hours.Count / 2]) / 2;
        return new RequestStats(await opened.CountAsync(ct), await opened.CountAsync(r => r.NeededBy < today, ct), await opened.CountAsync(r => r.AssigneeUserId == null, ct),
            median is { } m ? Math.Round(m, 1) : null, await (await InMyDirectionsAsync(db, opened, user.UserId(), ct)).CountAsync(ct));
    }

    private static async Task<List<RequestAssignee>> AssigneesAsync(ToadFlowDb db, CancellationToken ct) =>
        await db.Users.AsNoTracking().Where(u => !u.IsDisabled && !u.IsPending && (u.Role == UserRole.Operator || u.Role == UserRole.Admin))
            .OrderBy(u => u.DisplayName).Select(u => new RequestAssignee(u.Id, u.DisplayName)).ToListAsync(ct);

    private static async Task<IResult> GetAsync(Guid id, ToadFlowDb db, ClaimsPrincipal user, ISettingsStore settings, TimeProvider time,
        FamiliesSettingsStore familiesSettings, CancellationToken ct)
    {
        var request = await db.Set<FamilyRequest>().AsNoTracking().SingleOrDefaultAsync(r => r.Id == id, ct);
        if (request is null) return Results.NotFound();

        var me = user.UserId();
        var members = await db.Set<FamilyRequestMember>().AsNoTracking().Where(m => m.RequestId == id).OrderBy(m => m.JoinedAt).ToListAsync(ct);
        // Сообщение и строка о событии одного действия пишутся с одним моментом, а Id внутри миллисекунды не растут: событие — после сообщения.
        var messages = await db.Set<FamilyRequestMessage>().AsNoTracking().Where(m => m.RequestId == id)
            .OrderBy(m => m.At).ThenBy(m => m.IsEvent).ThenBy(m => m.Id).ToListAsync(ct);
        var files = await db.Set<FamilyRequestFile>().AsNoTracking().Where(f => f.RequestId == id).OrderBy(f => f.At).ThenBy(f => f.Id).ToListAsync(ct);
        var names = await NamesAsync(db, members.Select(m => m.UserId).Concat(messages.Select(m => m.AuthorUserId).OfType<Guid>()).Concat(files.Select(f => f.UploadedBy)), ct);
        var item = (await ItemsAsync(db, [request], me, await TodayAsync(settings, time, ct), time.GetUtcNow(), ct))[0];
        DateTimeOffset? autoAccept = request is { State: RequestState.Review, ReviewSince: { } since }
            ? since.AddDays((await familiesSettings.GetAsync(ct)).AutoAcceptDays)
            : null;
        return Results.Ok(new RequestView(item, request.Text, request.Reason,
            members.Select(m => new RequestMemberView(m.UserId, names.GetValueOrDefault(m.UserId, "?"))).ToList(),
            messages.Select(m => new RequestMessageView(m.Id, m.AuthorUserId, m.AuthorUserId is { } a ? names.GetValueOrDefault(a, "?") : null, m.Text, m.IsEvent, m.At,
                files.Where(f => f.MessageId == m.Id).Select(f => f.Id).ToList())).ToList(),
            files.Select(f => new RequestFileView(f.Id, f.FileName, f.Size, f.ContentType, names.GetValueOrDefault(f.UploadedBy, "?"), f.At, f.MessageId, f.UploadedBy,
                RequestFileTypes.IsInline(f.ContentType))).ToList(),
            autoAccept));
    }

    private static async Task<IResult> SeenAsync(Guid id, ToadFlowDb db, ClaimsPrincipal user, TimeProvider time, CancellationToken ct)
    {
        var me = user.UserId();
        await db.Set<FamilyRequestMember>().Where(m => m.RequestId == id && m.UserId == me).ExecuteUpdateAsync(m => m.SetProperty(x => x.SeenAt, time.GetUtcNow()), ct);
        return Results.Ok();
    }

    internal static async Task<List<RequestListItem>> ItemsAsync(ToadFlowDb db, IReadOnlyList<FamilyRequest> rows, Guid me, DateOnly today, DateTimeOffset now,
        CancellationToken ct)
    {
        if (rows.Count == 0) return [];
        var ids = rows.Select(r => r.Id).ToList();
        var members = await db.Set<FamilyRequestMember>().AsNoTracking().Where(m => ids.Contains(m.RequestId)).ToListAsync(ct);
        var cardIds = rows.Select(r => r.CardId).Concat(rows.Select(r => r.TargetCardId)).OfType<Guid>().Distinct().ToList();
        var cards = await db.Set<FamilyCard>().AsNoTracking().Where(c => cardIds.Contains(c.Id)).ToDictionaryAsync(c => c.Id, c => c.Name, ct);
        var names = await NamesAsync(db, rows.Select(r => r.AuthorUserId).Concat(rows.Select(r => r.AssigneeUserId).OfType<Guid>()), ct);
        var directionIds = rows.Select(r => r.DirectionId).OfType<Guid>().Distinct().ToList();
        var directions = await db.Set<FamilyDirection>().AsNoTracking().Where(d => directionIds.Contains(d.Id)).ToDictionaryAsync(d => d.Id, d => d.Name, ct);
        var thumbnails = (await db.Set<FamilyRequestFile>().AsNoTracking()
                .Where(f => f.RequestId != null && ids.Contains(f.RequestId.Value) && f.ContentType.StartsWith("image/"))
                .OrderBy(f => f.At).ThenBy(f => f.Id).Select(f => new { f.RequestId, f.Id }).ToListAsync(ct))
            .GroupBy(f => f.RequestId!.Value).ToDictionary(g => g.Key, g => g.First().Id);
        return rows.Select(r =>
        {
            var mine = members.FirstOrDefault(m => m.RequestId == r.Id && m.UserId == me);
            var unread = mine is not null && r.UpdatedAt > (mine.SeenAt ?? mine.JoinedAt);
            var turn = r.State switch
            {
                RequestState.New or RequestState.InWork => "staff",
                RequestState.NeedInfo or RequestState.Review => "author",
                _ => null
            };
            var waiting = (int)((r.ClosedAt ?? now) - r.CreatedAt).TotalDays;
            return new RequestListItem(r.Id, r.Number, r.Title, r.Section, r.BlocksIssue ? RequestUrgency.Urgent : RequestUrgency.Normal, r.Project, r.State,
                r.AuthorUserId, names.GetValueOrDefault(r.AuthorUserId, "?"), r.AssigneeUserId, r.AssigneeUserId is { } a ? names.GetValueOrDefault(a, "?") : null,
                r.CardId, r.CardId is { } c ? cards.GetValueOrDefault(c) : null, members.Count(m => m.RequestId == r.Id), mine is not null, unread, r.CreatedAt,
                r.UpdatedAt, r.Kind, r.DirectionId, r.DirectionId is { } d ? directions.GetValueOrDefault(d) : null, r.NeededBy, r.PromisedBy, r.BlocksIssue,
                r.IsOpen && r.NeededBy < today, turn, Math.Max(waiting, 0), thumbnails.TryGetValue(r.Id, out var thumb) ? thumb : null, r.RevitYear,
                r.TargetCardId, r.TargetCardId is { } t ? cards.GetValueOrDefault(t) : null, r.Resolution, r.ReviewSince);
        }).ToList();
    }
}
