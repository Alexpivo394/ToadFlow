using System.Globalization;
using System.IO.Compression;
using Microsoft.Extensions.Options;
using ToadFlow.Server.Clash.Collisions;
using ToadFlow.Server.Clash.Images;
using ToadFlow.Server.Data;
using ToadFlow.Server.Settings;

namespace ToadFlow.Server.Clash.Reports;

/// <summary>Подготовка архива выгрузки, как её видит веб.</summary>
/// <param name="Discipline">Раздел архива; null — все пары.</param>
/// <param name="State"><see cref="ReportArchives.Queued" />, <see cref="ReportArchives.Running" />, <see cref="ReportArchives.Ready" />,
///     <see cref="ReportArchives.Failed" /> или <see cref="ReportArchives.Expired" />.</param>
/// <param name="QueuePosition">Сколько подготовок впереди в очереди; 0 — не в очереди.</param>
public sealed record ReportArchiveView(
    Guid Id,
    Guid RunId,
    string? Discipline,
    string State,
    int PairsDone,
    int PairsTotal,
    int QueuePosition,
    long? SizeBytes,
    DateTimeOffset? ExpiresAt,
    string? Error);

/// <summary>
///     Архивы HTML-выгрузки раздела или всех пар: по файлу на пару и оглавление <c>index.html</c>. Готовятся в фоне, не больше
///     <see cref="MaxConcurrent" /> одновременно, у пользователя — одна подготовка (новая заменяет прежнюю). Готовый архив
///     хранится <see cref="Lifetime" /> в <c>{Root}/exports</c>. Состояние — в памяти: после перезапуска сервера подготовки
///     пропадают, файлы прошлого запуска удаляются.
/// </summary>
public sealed class ReportArchives(IServiceScopeFactory scopes, IOptions<ClashStorageOptions> storage, TimeProvider time, ILogger<ReportArchives> logger)
    : BackgroundService
{
    public const int MaxConcurrent = 2;
    public static readonly TimeSpan Lifetime = TimeSpan.FromDays(1);
    private static readonly TimeSpan CleanPeriod = TimeSpan.FromHours(1);

    public const string Queued = "queued";
    public const string Running = "running";
    public const string Ready = "ready";
    public const string Failed = "failed";
    public const string Expired = "expired";

    private readonly Lock _lock = new();
    private readonly Dictionary<Guid, Job> _byUser = [];
    private readonly List<Job> _queue = [];
    private readonly SemaphoreSlim _slots = new(MaxConcurrent, MaxConcurrent);

    private string Folder => Path.Combine(storage.Value.Root, "exports");

    private sealed class Job
    {
        public required Guid Id { get; init; }
        public required Guid UserId { get; init; }
        public required Guid RunId { get; init; }
        public required string? Discipline { get; init; }
        public required string Scope { get; init; }
        public required CollisionFilter Filter { get; init; }
        public required string? Sort { get; init; }
        public required string Web { get; init; }
        public CancellationTokenSource Cancel { get; } = new();
        public string State { get; set; } = Queued;
        public int PairsDone { get; set; }
        public int PairsTotal { get; set; }
        public long? SizeBytes { get; set; }
        public DateTimeOffset? ExpiresAt { get; set; }
        public string? Error { get; set; }
        public string? FileName { get; set; }
    }

    /// <summary>Ставит подготовку архива; прежняя подготовка пользователя отменяется, её файл удаляется.</summary>
    /// <param name="discipline">Раздел; null — все пары выборки.</param>
    /// <param name="filter">Фильтры списка отчёта без пары и раздела.</param>
    public ReportArchiveView Start(Guid userId, Guid runId, string? discipline, string scope, CollisionFilter filter, string? sort, string web)
    {
        var job = new Job
        {
            Id = Guid.CreateVersion7(), UserId = userId, RunId = runId, Discipline = discipline, Scope = scope,
            Filter = filter with { Discipline = discipline, Pair = null }, Sort = sort, Web = web
        };
        lock (_lock)
        {
            if (_byUser.Remove(userId, out var previous)) Discard(previous);
            _byUser[userId] = job;
            _queue.Add(job);
        }

        _ = Task.Run(() => RunAsync(job));
        return View(job);
    }

    /// <summary>Последняя подготовка пользователя; null — её нет.</summary>
    public ReportArchiveView? Current(Guid userId)
    {
        lock (_lock) return _byUser.TryGetValue(userId, out var job) ? View(job) : null;
    }

    /// <summary>Отменяет подготовку пользователя и удаляет её файл; false — такой нет.</summary>
    public bool Cancel(Guid userId, Guid id)
    {
        lock (_lock)
        {
            if (!_byUser.TryGetValue(userId, out var job) || job.Id != id) return false;
            _byUser.Remove(userId);
            Discard(job);
            return true;
        }
    }

    /// <summary>Готовый архив пользователя: путь и имя файла; <c>Gone</c> — срок вышел; null — нет такого готового.</summary>
    public (string Path, string FileName)? Download(Guid userId, Guid id, out bool gone)
    {
        lock (_lock)
        {
            gone = false;
            if (!_byUser.TryGetValue(userId, out var job) || job.Id != id) return null;
            if (job.State == Expired || (job.State == Ready && job.ExpiresAt < time.GetUtcNow()))
            {
                gone = true;
                return null;
            }

            return job.State == Ready ? (Final(job), job.FileName!) : null;
        }
    }

    /// <summary>Удаляет архивы с истёкшим сроком; подготовка остаётся видна как <see cref="Expired" />.</summary>
    public void Clean()
    {
        lock (_lock)
        {
            foreach (var job in _byUser.Values.Where(j => j.State == Ready && j.ExpiresAt < time.GetUtcNow()))
            {
                job.State = Expired;
                Delete(Final(job));
            }
        }
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        // Подготовки прошлого запуска потеряны вместе с памятью — их файлы не нужны.
        if (Directory.Exists(Folder))
            foreach (var file in Directory.EnumerateFiles(Folder))
                Delete(file);

        using var timer = new PeriodicTimer(CleanPeriod, time);
        try
        {
            while (await timer.WaitForNextTickAsync(ct)) Clean();
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            lock (_lock)
                foreach (var job in _byUser.Values)
                    job.Cancel.Cancel();
        }
    }

    private ReportArchiveView View(Job job)
    {
        var position = _queue.IndexOf(job);
        return new ReportArchiveView(job.Id, job.RunId, job.Discipline, job.State, job.PairsDone, job.PairsTotal, position < 0 ? 0 : position, job.SizeBytes,
            job.ExpiresAt, job.Error);
    }

    /// <summary>Под блокировкой: отменяет подготовку; готовый файл удаляет сразу, недописанный удалит сама подготовка.</summary>
    private void Discard(Job job)
    {
        _queue.Remove(job);
        job.Cancel.Cancel();
        if (job.State == Ready) Delete(Final(job));
    }

    private string Final(Job job) => Path.Combine(Folder, $"{job.Id:N}.zip");

    private async Task RunAsync(Job job)
    {
        try
        {
            await _slots.WaitAsync(job.Cancel.Token);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        var partial = Final(job) + ".partial";
        try
        {
            lock (_lock)
            {
                _queue.Remove(job);
                if (job.Cancel.IsCancellationRequested) return;
                job.State = Running;
            }

            Directory.CreateDirectory(Folder);
            await WriteAsync(job, partial, job.Cancel.Token);
            lock (_lock)
            {
                if (job.Cancel.IsCancellationRequested)
                {
                    Delete(partial);
                    return;
                }

                System.IO.File.Move(partial, Final(job), true);
                job.SizeBytes = new FileInfo(Final(job)).Length;
                job.ExpiresAt = time.GetUtcNow() + Lifetime;
                job.State = Ready;
            }
        }
        catch (Exception exception)
        {
            Delete(partial);
            if (exception is OperationCanceledException && job.Cancel.IsCancellationRequested) return;
            logger.LogError(exception, "Не удалось подготовить архив отчёта {RunId}", job.RunId);
            lock (_lock)
            {
                job.State = Failed;
                job.Error = exception.Message;
            }
        }
        finally
        {
            _slots.Release();
        }
    }

    private async Task WriteAsync(Job job, string path, CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var services = scope.ServiceProvider;
        var reports = services.GetRequiredService<RunReports>();
        var db = services.GetRequiredService<ToadFlowDb>();
        var images = services.GetRequiredService<CollisionImages>();
        var context = await reports.ContextAsync(job.RunId, ct) ?? throw new InvalidOperationException("Прогон не найден");
        var view = await reports.ViewAsync(job.RunId, ct) ?? throw new InvalidOperationException("Отчёт не найден");
        var zone = (await services.GetRequiredService<ISettingsStore>().GetAsync(ct)).TimeZone;

        var ranks = await reports.RanksAsync(context.Run.PartId, ct);
        var order = await reports.OrderAsync(context, job.Scope, job.Filter, ranks, ct);
        lock (_lock) job.PairsTotal = order.Pairs.Length;
        var day = TimeZoneInfo.ConvertTime(view.Run.FinishedAt, zone).ToString("dd.MM.yyyy", CultureInfo.InvariantCulture);
        job.FileName = ReportPairFile.FileName(["Коллизии", view.Check.Object, view.Check.Name, job.Discipline ?? "всё", day]) + ".zip";

        var digits = Math.Max(2, order.Pairs.Length.ToString(CultureInfo.InvariantCulture).Length);
        var files = new List<ReportArchiveIndex.Entry>();
        await using (var stream = System.IO.File.Create(path))
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create))
        {
            foreach (var (pair, i) in order.Pairs.Select((p, i) => (p, i)))
            {
                var pairView = ReportPairFile.Pair(view, ranks, pair);
                var name = ReportPairFile.FileName([(i + 1).ToString(CultureInfo.InvariantCulture).PadLeft(digits, '0'), pairView.Label]) + ".html";
                var selection = await reports.SelectAsync(context, job.Scope, job.Filter with { Pair = pair }, job.Sort, ct);
                int total;
                await using (var entry = zip.CreateEntry(name, CompressionLevel.Fastest).Open())
                    total = await ReportPairFile.WriteAsync(entry, view, context, selection, pair, db, images, job.Web, zone, ct);
                files.Add(new ReportArchiveIndex.Entry(name, pairView, total, Math.Min(total, ReportHtml.MaxImages)));
                lock (_lock) job.PairsDone = i + 1;
            }

            await using var index = zip.CreateEntry("index.html", CompressionLevel.Fastest).Open();
            await ReportArchiveIndex.WriteAsync(index, view, job.Discipline, files, ranks, job.Web, zone, ct);
        }
    }

    private void Delete(string file)
    {
        try
        {
            System.IO.File.Delete(file);
        }
        catch (IOException exception)
        {
            logger.LogWarning(exception, "Не удалось удалить файл архива {File}", file);
        }
    }
}
