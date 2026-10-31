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
public sealed record ReportRunView(
    Guid RunId,
    Guid PartId,
    int Version,
    DateTimeOffset FinishedAt,
    string Trigger,
    string? StartedBy,
    double SearchSeconds,
    ApprovedRunSummary? Summary = null);

/// <param name="UploadedAt">Когда выгружен слепок, по которому шёл прогон.</param>
/// <param name="Unchecked">Модель в этом цикле не выгрузилась: её пары «не проверены».</param>
public sealed record ReportModelView(string Name, string Discipline, DateTimeOffset? UploadedAt, bool Unchecked);

/// <param name="Open">Открытых сейчас.</param>
/// <param name="New">Новых в этом прогоне.</param>
/// <param name="Returned">Вернулись в этом прогоне.</param>
/// <param name="Fixed">Исправлены в этом прогоне.</param>
/// <param name="Fake">Фиктивных сейчас.</param>
public sealed record ReportPairView(string Pair, int Open, int New, int Returned, int Fixed, int Fake);

/// <summary>Отчёт утверждённого прогона: шапка, сводка, пары разделов и ссылка на более новый прогон, если он есть.</summary>
public sealed record ReportView(
    ReportRunView Run,
    CheckView Check,
    ApprovedRunSummary Summary,
    IReadOnlyList<ReportModelView> Models,
    IReadOnlyList<ReportPairView> Pairs,
    Guid? NewerRunId,
    DateTimeOffset? NewerRunAt);

/// <summary>Строка отчёта: текущее состояние коллизии и её переход в этом прогоне.</summary>
/// <param name="Change">Статус, в который коллизия перешла в этом прогоне; <c>New</c> — найдена им впервые; null — не менялась.</param>
public sealed record ReportRow(CollisionRow Collision, CollisionStatus? Change, string? LastNote, string? LastNoteBy, DateTimeOffset? LastNoteAt);

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
public sealed class RunReports(ToadFlowDb db, PartModels models, TimeProvider time)
{
    public const string Changes = "changes";
    public const string Open = "open";
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
        return new ReportView(views[0] with { Summary = null }, check, run.Summary ?? new ApprovedRunSummary(0, 0, 0, 0, 0, 0, 0, 0, 0), reportModels,
            await PairsAsync(run, ct), newer?.Id, newer?.FinishedAt);
    }

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
        return runs.Select(r => new ReportRunView(r.Id, r.PartId, r.Version ?? 0, r.FinishedAt ?? r.CreatedAt,
                r.TriggerRunId is null ? "manual" : "schedule",
                r.TriggerRunId is { } trigger ? schedules.GetValueOrDefault(trigger) : r.StartedBy,
                r.Stats is { } stats ? stats.BroadSeconds + stats.NarrowSeconds : 0,
                r.Summary))
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
    ///     Строки отчёта по фильтру и сортировке — один источник для страницы и выгрузок. В «изменениях» без фильтра статуса
    ///     видны все статусы (исправленные тоже), в «открытых» — открытые.
    /// </summary>
    public IQueryable<ReportRowSource> Rows(ReportContext context, string scope, CollisionFilter filter, string? sort)
    {
        var run = context.Run;
        if (scope == Changes && string.IsNullOrWhiteSpace(filter.Status)) filter = filter with { Status = "all" };
        var filtered = filter.Apply(Scope(run, scope), db, context.ModelNames, time.GetUtcNow());
        var runId = run.Id;
        var notable = Notable(runId);
        var notes = db.Set<ClashCollisionEvent>().AsNoTracking().Where(e => e.Kind == CollisionEventKind.Note);
        var users = db.Users.AsNoTracking();
        return CollisionFilter.Sort(filtered, sort).Select(c => new ReportRowSource
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

    public static ReportRow Row(ReportRowSource source, IReadOnlyDictionary<string, string> names, IReadOnlyDictionary<Guid, int> suggested) =>
        new(CollisionEndpoints.Row(source.Collision, names, suggested), source.Change, source.LastNote, source.LastNoteBy, source.LastNoteAt);

    /// <summary>Пары разделов: открытые и фиктивные — сейчас, новые, вернувшиеся и исправленные — в этом прогоне.</summary>
    private async Task<IReadOnlyList<ReportPairView>> PairsAsync(EngineRun run, CancellationToken ct)
    {
        var open = CollisionEndpoints.Open;
        var current = await db.Set<ClashCollision>().AsNoTracking().Where(c => c.PartId == run.PartId)
            .GroupBy(c => c.DisciplinePair)
            .Select(g => new { Pair = g.Key, Open = g.Count(c => open.Contains(c.Status)), Fake = g.Count(c => c.Status == CollisionStatus.Fake) })
            .ToListAsync(ct);
        var context = new ReportContext(run, new Dictionary<string, string>());
        var changes = await Rows(context, Changes, new CollisionFilter(null, null, null, null, null, null, null, null, null), null)
            .Select(r => new { r.Collision.DisciplinePair, r.Change }).ToListAsync(ct);

        return current.Select(c => c.Pair).Concat(changes.Select(c => c.DisciplinePair)).Distinct()
            .Select(pair =>
            {
                int Moved(CollisionStatus status) => changes.Count(c => c.DisciplinePair == pair && c.Change == status);
                var now = current.FirstOrDefault(c => c.Pair == pair);
                return new ReportPairView(pair, now?.Open ?? 0, Moved(CollisionStatus.New), Moved(CollisionStatus.Returned), Moved(CollisionStatus.Fixed), now?.Fake ?? 0);
            })
            .Where(p => p.Open + p.New + p.Returned + p.Fixed + p.Fake > 0)
            .OrderByDescending(p => p.Open).ThenBy(p => p.Pair, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }
}
