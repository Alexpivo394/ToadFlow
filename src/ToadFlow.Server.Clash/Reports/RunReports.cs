using Microsoft.EntityFrameworkCore;
using ToadFlow.Server.Clash.Collisions;
using ToadFlow.Server.Clash.Engine;
using ToadFlow.Server.Clash.Parts;
using ToadFlow.Server.Data;

namespace ToadFlow.Server.Clash.Reports;

/// <summary>Прогон в шапке отчёта и в списке прогонов проверки.</summary>
/// <param name="Trigger"><c>schedule</c> — по расписанию, <c>manual</c> — вручную.</param>
/// <param name="StartedBy">Расписание или пользователь; null — не известно.</param>
/// <param name="SearchSeconds">Время поиска движка (широкая и точная фазы).</param>
/// <param name="Summary">Итог сверки — в списке прогонов; в <see cref="ReportView" /> он отдельным полем.</param>
/// <param name="Comparable">У прогона есть счётчики по парам (<see cref="Collisions.ClashRunPairStat" />) — его можно сравнить с другим.</param>
public sealed record ReportRunView(
    Guid RunId,
    Guid PartId,
    int Version,
    DateTimeOffset FinishedAt,
    string Trigger,
    string? StartedBy,
    double SearchSeconds,
    ApprovedRunSummary? Summary = null,
    bool Comparable = false);

/// <param name="UploadedAt">Когда выгружен слепок, по которому шёл прогон.</param>
/// <param name="Unchecked">Модель в этом цикле не выгрузилась: её пары «не проверены».</param>
public sealed record ReportModelView(string Name, string Discipline, DateTimeOffset? UploadedAt, bool Unchecked);

/// <param name="Pair">Ключ пары (<see cref="ClashCollision.DisciplinePair" />) — для фильтра и адреса.</param>
/// <param name="Label">Подпись пары в порядке справочника разделов: <c>ОВ × ВК</c>.</param>
/// <param name="DisciplineA">Первый раздел пары в порядке справочника.</param>
/// <param name="Open">Открытых сейчас.</param>
/// <param name="New">Новых в этом прогоне.</param>
/// <param name="Returned">Вернулись в этом прогоне.</param>
/// <param name="Fixed">Исправлены в этом прогоне.</param>
/// <param name="Fake">Фиктивных сейчас.</param>
public sealed record ReportPairView(string Pair, string Label, string DisciplineA, string DisciplineB, int Open, int New, int Returned, int Fixed, int Fake);

/// <summary>Раздел в дереве отчёта: суммы по его парам.</summary>
/// <param name="Pairs">Ключи пар раздела в порядке отчёта.</param>
public sealed record ReportDisciplineView(string Name, int Open, int New, int Returned, int Fixed, IReadOnlyList<string> Pairs);

/// <summary>Отчёт утверждённого прогона: шапка, сводка, пары разделов и ссылка на более новый прогон, если он есть.</summary>
/// <param name="Pairs">Пары в порядке справочника разделов.</param>
/// <param name="Disciplines">Разделы пар в порядке справочника, у каждого — его пары.</param>
public sealed record ReportView(
    ReportRunView Run,
    CheckView Check,
    ApprovedRunSummary Summary,
    IReadOnlyList<ReportModelView> Models,
    IReadOnlyList<ReportPairView> Pairs,
    IReadOnlyList<ReportDisciplineView> Disciplines,
    Guid? NewerRunId,
    DateTimeOffset? NewerRunAt);

/// <summary>Строка отчёта: текущее состояние коллизии и её переход в этом прогоне.</summary>
/// <param name="Change">Статус, в который коллизия перешла в этом прогоне; <c>New</c> — найдена им впервые; null — не менялась.</param>
/// <param name="Level">Уровень строки для группировки (<see cref="CollisionLevel.Key" />); пусто — без уровня.</param>
public sealed record ReportRow(CollisionRow Collision, CollisionStatus? Change, string? LastNote, string? LastNoteBy, DateTimeOffset? LastNoteAt, string Level);

/// <summary>Уровень в паре: заголовок группы строк отчёта и пункт фильтра уровня.</summary>
/// <param name="Pair">Ключ пары.</param>
/// <param name="Level">Уровень (<see cref="CollisionLevel.Key" />); пусто — без уровня.</param>
/// <param name="Count">Строк уровня в выборке.</param>
public sealed record ReportLevelView(string Pair, string Level, int Count);

/// <summary>Выборка отчёта: строки в порядке и порядок, по которому они идут.</summary>
public sealed record ReportSelection(IQueryable<ReportRowSource> Rows, ReportOrder Order, DisciplineRanks Ranks);

/// <summary>Порядок выборки отчёта: ключи пар и «пара␟уровень» по порядку, и уровни с числами.</summary>
public sealed record ReportOrder(string[] Pairs, string[] Levels, IReadOnlyList<ReportLevelView> LevelViews);

public sealed record ReportPage(int Total, IReadOnlyList<ReportRow> Items);

public sealed record ChatMessageView(string Text);

/// <summary>Строка отчёта, как её отдаёт база: коллизия, переход и последнее примечание (подзапросы на строку).</summary>
public sealed class ReportRowSource
{
    public ClashCollision Collision { get; init; } = null!;
    public CollisionStatus? Change { get; init; }
    public string? LastNote { get; init; }
    public string? LastNoteBy { get; init; }
    public DateTimeOffset? LastNoteAt { get; init; }
}

/// <summary>Утверждённый прогон, по которому строится отчёт, и имена моделей его части.</summary>
public sealed record ReportContext(EngineRun Run, IReadOnlyDictionary<string, string> ModelNames);

/// <summary>
///     Отчёт утверждённого прогона. Коллизии живут в части, а не в прогоне: «изменения прогона» — коллизии, найденные им
///     впервые (<see cref="ClashCollision.FirstRunId" />), и коллизии с заметным переходом статуса в его сверке (запись ленты с
///     <see cref="ClashCollisionEvent.RunId" />); «открытые сейчас» — открытые коллизии части. Статусы в строках — текущие.
/// </summary>
public sealed class RunReports(ToadFlowDb db, PartModels models, DisciplineOrder order, TimeProvider time)
{
    public const string Changes = "changes";
    public const string Open = "open";
    public const string Level = "level";
    public const string Depth = "depth";
    public const string Number = "number";
    public const int MaxRuns = 100;

    /// <summary>Удачный утверждённый прогон и имена моделей части; null — прогона нет, он черновика или не закончился удачно.</summary>
    public async Task<ReportContext?> ContextAsync(Guid runId, CancellationToken ct)
    {
        var run = await db.Set<EngineRun>().AsNoTracking()
            .SingleOrDefaultAsync(r => r.Id == runId && r.Kind == EngineRunKind.Approved && r.State == EngineRunState.Succeeded, ct);
        return run is null ? null : new ReportContext(run, await CollisionEndpoints.ModelNamesAsync(db, run.PartId, ct));
    }

    public async Task<ReportView?> ViewAsync(Guid runId, CancellationToken ct)
    {
        if (await ContextAsync(runId, ct) is not { } context) return null;
        var run = context.Run;
        if ((await CheckEndpoints.ChecksAsync(db, models, run.PartId, ct)).SingleOrDefault() is not { } check) return null;

        var snapshotIds = run.Models.Select(m => m.SnapshotId).ToList();
        var uploaded = await db.Set<ClashSnapshot>().AsNoTracking().Where(s => snapshotIds.Contains(s.Id))
            .ToDictionaryAsync(s => s.Id, s => s.ExtractedAt, ct);
        var reportModels = run.Models
            .Select(m => new ReportModelView(m.ModelName, m.Discipline, uploaded.TryGetValue(m.SnapshotId, out var at) ? at : null, run.UncheckedModels.Contains(m.ModelId)))
            .OrderBy(m => m.Name, StringComparer.CurrentCultureIgnoreCase).ToList();

        var newer = await db.Set<EngineRun>().AsNoTracking()
            .Where(r => r.PartId == run.PartId && r.Kind == EngineRunKind.Approved && r.State == EngineRunState.Succeeded && r.FinishedAt > run.FinishedAt)
            .OrderByDescending(r => r.FinishedAt).Select(r => new { r.Id, r.FinishedAt }).FirstOrDefaultAsync(ct);

        var views = await RunViewsAsync([run], ct);
        var ranks = await RanksAsync(run.PartId, ct);
        var pairs = await PairsAsync(run, ranks, ct);
        return new ReportView(views[0] with { Summary = null }, check, run.Summary ?? new ApprovedRunSummary(0, 0, 0, 0, 0, 0, 0, 0, 0), reportModels,
            pairs, Disciplines(pairs, ranks), newer?.Id, newer?.FinishedAt);
    }

    /// <summary>Ранги разделов части: справочник именования и разделы, которые встречаются в коллизиях части.</summary>
    public async Task<DisciplineRanks> RanksAsync(Guid partId, CancellationToken ct)
    {
        var present = await db.Set<ClashCollision>().AsNoTracking().Where(c => c.PartId == partId)
            .Select(c => c.DisciplinePair).Distinct().ToListAsync(ct);
        return await order.LoadAsync(present.SelectMany(p =>
        {
            var (a, b) = DisciplineRanks.Split(p);
            return new[] { a, b };
        }), ct);
    }

    private static IReadOnlyList<ReportDisciplineView> Disciplines(IReadOnlyList<ReportPairView> pairs, DisciplineRanks ranks) =>
        pairs.SelectMany(p => new[] { p.DisciplineA, p.DisciplineB }).Distinct().OrderBy(ranks.Of)
            .Select(name =>
            {
                var own = pairs.Where(p => p.DisciplineA == name || p.DisciplineB == name).ToList();
                return new ReportDisciplineView(name, own.Sum(p => p.Open), own.Sum(p => p.New), own.Sum(p => p.Returned), own.Sum(p => p.Fixed),
                    own.Select(p => p.Pair).ToList());
            })
            .ToList();

    /// <summary>Удачные утверждённые прогоны части, новые сверху, с итогом сверки.</summary>
    public async Task<IReadOnlyList<ReportRunView>> RunsAsync(Guid partId, CancellationToken ct)
    {
        var runs = await db.Set<EngineRun>().AsNoTracking()
            .Where(r => r.PartId == partId && r.Kind == EngineRunKind.Approved && r.State == EngineRunState.Succeeded)
            .OrderByDescending(r => r.FinishedAt).Take(MaxRuns).ToListAsync(ct);
        return await RunViewsAsync(runs, ct);
    }

    private async Task<List<ReportRunView>> RunViewsAsync(IReadOnlyList<EngineRun> runs, CancellationToken ct)
    {
        var triggers = runs.Select(r => r.TriggerRunId).OfType<Guid>().Distinct().ToList();
        var schedules = triggers.Count == 0
            ? []
            : await db.Runs.AsNoTracking().Where(r => triggers.Contains(r.Id) && r.ScheduleId != null)
                .Join(db.Schedules.AsNoTracking(), r => r.ScheduleId, s => s.Id, (r, s) => new { r.Id, s.Name })
                .ToDictionaryAsync(r => r.Id, r => r.Name, ct);
        var ids = runs.Select(r => r.Id).ToList();
        var comparable = (await db.Set<ClashRunPairStat>().AsNoTracking()
            .Where(s => ids.Contains(s.RunId) && s.DisciplinePair == ClashRunPairStat.Marker).Select(s => s.RunId).ToListAsync(ct)).ToHashSet();
        return runs.Select(r => new ReportRunView(r.Id, r.PartId, r.Version ?? 0, r.FinishedAt ?? r.CreatedAt,
                r.TriggerRunId is null ? "manual" : "schedule",
                r.TriggerRunId is { } trigger ? schedules.GetValueOrDefault(trigger) : r.StartedBy,
                r.Stats is { } stats ? stats.BroadSeconds + stats.NarrowSeconds : 0,
                r.Summary, comparable.Contains(r.Id)))
            .ToList();
    }

    /// <summary>
    ///     Записи сверки прогона, которые меняют картину: вернулась, исправлена, ушла из-за правил, не проверена, снова активная
    ///     после «исправлена (ждёт)». Новая → активная (просто найдена ещё раз) — не изменение.
    /// </summary>
    private IQueryable<ClashCollisionEvent> Notable(Guid runId) =>
        db.Set<ClashCollisionEvent>().AsNoTracking().Where(e => e.RunId == runId && e.Kind == CollisionEventKind.Status
                                                                && (e.ToStatus == CollisionStatus.Returned || e.ToStatus == CollisionStatus.Fixed
                                                                    || e.ToStatus == CollisionStatus.GoneByRules || e.ToStatus == CollisionStatus.Unchecked
                                                                    || (e.ToStatus == CollisionStatus.Active && e.FromStatus == CollisionStatus.FixedPending)));

    /// <summary>Коллизии области отчёта до фильтров: изменения прогона или открытые сейчас.</summary>
    private IQueryable<ClashCollision> Scope(EngineRun run, string scope)
    {
        var collisions = db.Set<ClashCollision>().AsNoTracking().Where(c => c.PartId == run.PartId);
        if (scope == Open) return collisions;
        var runId = run.Id;
        var notable = Notable(runId);
        return collisions.Where(c => c.FirstRunId == runId || notable.Any(e => e.CollisionId == c.Id));
    }

    /// <summary>
    ///     Коллизии области по фильтру, без порядка. В «изменениях» без фильтра статуса видны все статусы (исправленные тоже), в
    ///     «открытых» — открытые.
    /// </summary>
    private IQueryable<ClashCollision> Filtered(ReportContext context, string scope, CollisionFilter filter)
    {
        if (scope == Changes && string.IsNullOrWhiteSpace(filter.Status)) filter = filter with { Status = "all" };
        return filter.Apply(Scope(context.Run, scope), db, context.ModelNames, time.GetUtcNow());
    }

    /// <summary>
    ///     Порядок пар и уровней выборки: пары — по справочнику разделов, уровни внутри пары — снизу вверх по наименьшему Z
    ///     коллизий уровня, без уровня — последним. Один групповой запрос; страница и выгрузки сортируют по нему.
    /// </summary>
    public async Task<ReportOrder> OrderAsync(ReportContext context, string scope, CollisionFilter filter, DisciplineRanks ranks, CancellationToken ct)
    {
        var groups = await Filtered(context, scope, filter)
            .Select(c => new
            {
                c.DisciplinePair,
                c.Z,
                Level = c.LevelA == c.LevelB || c.LevelB == null ? c.LevelA ?? "" : c.LevelA == null ? c.LevelB : c.LevelA + " / " + c.LevelB
            })
            .GroupBy(x => new { x.DisciplinePair, x.Level })
            .Select(g => new { g.Key.DisciplinePair, g.Key.Level, MinZ = g.Min(x => x.Z), Count = g.Count() })
            .ToListAsync(ct);

        var pairs = groups.Select(g => g.DisciplinePair).Distinct()
            .Select(pair =>
            {
                var (a, b) = DisciplineRanks.Split(pair);
                return (Pair: pair, Rank: ranks.PairRank(a, b), Label: ranks.Label(a, b));
            })
            .OrderBy(p => p.Rank.First).ThenBy(p => p.Rank.Second).ThenBy(p => p.Label, StringComparer.CurrentCultureIgnoreCase)
            .Select(p => p.Pair).ToArray();
        var pairIndex = pairs.Select((p, i) => (p, i)).ToDictionary(x => x.p, x => x.i);
        var levels = groups
            .OrderBy(g => pairIndex[g.DisciplinePair]).ThenBy(g => g.Level.Length == 0).ThenBy(g => g.MinZ)
            .ThenBy(g => g.Level, StringComparer.CurrentCultureIgnoreCase)
            .Select(g => new ReportLevelView(g.DisciplinePair, g.Level, g.Count)).ToList();
        return new ReportOrder(pairs, levels.Select(l => CollisionLevel.Composite(l.Pair, l.Level)).ToArray(), levels);
    }

    /// <summary>Строки выборки в порядке отчёта вместе с самим порядком (ранги разделов, пары, уровни).</summary>
    public async Task<ReportSelection> SelectAsync(ReportContext context, string scope, CollisionFilter filter, string? sort, CancellationToken ct)
    {
        var ranks = await RanksAsync(context.Run.PartId, ct);
        var order = await OrderAsync(context, scope, filter, ranks, ct);
        return new ReportSelection(Rows(context, scope, filter, sort, order), order, ranks);
    }

    /// <summary>
    ///     Строки отчёта по фильтру и сортировке — один источник для страницы и выгрузок. Всегда сначала по паре в порядке
    ///     <paramref name="order" />; внутри пары <c>level</c> (по умолчанию) — уровень снизу вверх, потом №; <c>depth</c> — глубже
    ///     сначала; <c>number</c> — по №.
    /// </summary>
    public IQueryable<ReportRowSource> Rows(ReportContext context, string scope, CollisionFilter filter, string? sort, ReportOrder order) =>
        Source(context, Sorted(Filtered(context, scope, filter), sort, order));

    /// <summary>
    ///     Страница строк в порядке отчёта. Сначала Id страницы по голым коллизиям, потом переход и примечания только для них:
    ///     подзапросы строки иначе считаются и для всех пропущенных строк глубокой страницы.
    /// </summary>
    public async Task<(int Total, IReadOnlyList<ReportRowSource> Items)> PageAsync(ReportContext context, string scope, CollisionFilter filter, string? sort,
        ReportOrder order, int page, int size, CancellationToken ct)
    {
        var filtered = Filtered(context, scope, filter);
        var total = await filtered.CountAsync(ct);
        var ids = await Sorted(filtered, sort, order).Skip((page - 1) * size).Take(size).Select(c => c.Id).ToListAsync(ct);
        var rows = await Source(context, filtered.Where(c => ids.Contains(c.Id))).ToListAsync(ct);
        var index = ids.Select((id, i) => (id, i)).ToDictionary(x => x.id, x => x.i);
        return (total, rows.OrderBy(r => index[r.Collision.Id]).ToList());
    }

    private static IOrderedQueryable<ClashCollision> Sorted(IQueryable<ClashCollision> filtered, string? sort, ReportOrder order)
    {
        var byPair = filtered.OrderBy(CollisionLevel.PairPosition(order.Pairs));
        return sort switch
        {
            Depth => byPair.ThenByDescending(c => c.DepthMm).ThenBy(c => c.Number),
            Number => byPair.ThenBy(c => c.Number),
            _ => byPair.ThenBy(CollisionLevel.Position(order.Levels)).ThenBy(c => c.Number)
        };
    }

    private IQueryable<ReportRowSource> Source(ReportContext context, IQueryable<ClashCollision> collisions)
    {
        var runId = context.Run.Id;
        var notable = Notable(runId);
        var notes = db.Set<ClashCollisionEvent>().AsNoTracking().Where(e => e.Kind == CollisionEventKind.Note);
        var users = db.Users.AsNoTracking();
        return collisions.Select(c => new ReportRowSource
        {
            Collision = c,
            Change = c.FirstRunId == runId
                ? CollisionStatus.New
                : notable.Where(e => e.CollisionId == c.Id).OrderByDescending(e => e.At).Select(e => e.ToStatus).FirstOrDefault(),
            LastNote = notes.Where(e => e.CollisionId == c.Id).OrderByDescending(e => e.At).Select(e => e.Text).FirstOrDefault(),
            LastNoteAt = notes.Where(e => e.CollisionId == c.Id).OrderByDescending(e => e.At).Select(e => (DateTimeOffset?)e.At).FirstOrDefault(),
            LastNoteBy = notes.Where(e => e.CollisionId == c.Id).OrderByDescending(e => e.At)
                .Select(e => users.Where(u => u.Id == e.UserId).Select(u => u.DisplayName).FirstOrDefault()).FirstOrDefault()
        });
    }

    private static readonly Func<ClashCollision, string> LevelOf = CollisionLevel.Key.Compile();

    public static ReportRow Row(ReportRowSource source, IReadOnlyDictionary<string, string> names, IReadOnlyDictionary<Guid, int> suggested) =>
        new(CollisionEndpoints.Row(source.Collision, names, suggested), source.Change, source.LastNote, source.LastNoteBy, source.LastNoteAt,
            LevelOf(source.Collision));

    /// <summary>Пары разделов: открытые и фиктивные — сейчас, новые, вернувшиеся и исправленные — в этом прогоне.</summary>
    private async Task<IReadOnlyList<ReportPairView>> PairsAsync(EngineRun run, DisciplineRanks ranks, CancellationToken ct)
    {
        var open = CollisionEndpoints.Open;
        var current = await db.Set<ClashCollision>().AsNoTracking().Where(c => c.PartId == run.PartId)
            .GroupBy(c => c.DisciplinePair)
            .Select(g => new { Pair = g.Key, Open = g.Count(c => open.Contains(c.Status)), Fake = g.Count(c => c.Status == CollisionStatus.Fake) })
            .ToListAsync(ct);
        var context = new ReportContext(run, new Dictionary<string, string>());
        var changes = await Source(context, Filtered(context, Changes, new CollisionFilter(null, null, null, null, null, null, null, null, null)))
            .Select(r => new { r.Collision.DisciplinePair, r.Change }).ToListAsync(ct);

        return current.Select(c => c.Pair).Concat(changes.Select(c => c.DisciplinePair)).Distinct()
            .Select(pair =>
            {
                int Moved(CollisionStatus status) => changes.Count(c => c.DisciplinePair == pair && c.Change == status);
                var now = current.FirstOrDefault(c => c.Pair == pair);
                var (a, b) = DisciplineRanks.Split(pair);
                var (first, second) = ranks.Ordered(a, b);
                return new ReportPairView(pair, $"{first} × {second}", first, second, now?.Open ?? 0, Moved(CollisionStatus.New),
                    Moved(CollisionStatus.Returned), Moved(CollisionStatus.Fixed), now?.Fake ?? 0);
            })
            .Where(p => p.Open + p.New + p.Returned + p.Fixed + p.Fake > 0)
            .OrderBy(p => ranks.Of(p.DisciplineA)).ThenBy(p => ranks.Of(p.DisciplineB)).ThenBy(p => p.Label, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }
}
