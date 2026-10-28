using Microsoft.EntityFrameworkCore;
using ToadFlow.Server.Clash.Uploads;
using ToadFlow.Server.Data;

namespace ToadFlow.Server.Clash;

/// <summary>
///     Раз в час убирает хвосты загрузок: незавершённые загрузки старше суток (агент упал или задание ушло другому)
///     и папки в хранилище, которых нет в базе (сервер упал между переносом файлов и записью слепка).
/// </summary>
public sealed class ClashUploadCleaner(IServiceScopeFactory scopes, TimeProvider time, ILogger<ClashUploadCleaner> logger) : BackgroundService
{
    private static readonly TimeSpan Period = TimeSpan.FromHours(1);

    /// <summary>Моложе этого загрузка может продолжиться, а папка — получить запись в базе.</summary>
    private static readonly TimeSpan StaleAfter = TimeSpan.FromHours(24);

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(Period, time);
        do
        {
            try
            {
                await CleanAsync(ct);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                logger.LogError(exception, "Не удалось убрать брошенные загрузки слепков коллизий");
            }
        } while (await timer.WaitForNextTickAsync(ct));
    }

    public async Task CleanAsync(CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ToadFlowDb>();
        var storage = scope.ServiceProvider.GetRequiredService<ClashStorage>();
        var publisher = scope.ServiceProvider.GetRequiredService<SnapshotPublisher>();
        var cutoff = time.GetUtcNow() - StaleAfter;

        var stale = await db.Set<ClashUpload>().AsNoTracking().Where(u => u.CreatedAt < cutoff).ToListAsync(ct);
        foreach (var upload in stale) await publisher.DiscardAsync(upload, ct);
        if (stale.Count > 0) logger.LogInformation("Удалено брошенных загрузок слепков: {Count}", stale.Count);

        if (!Directory.Exists(storage.Root)) return;

        var uploads = (await db.Set<ClashUpload>().AsNoTracking().Select(u => u.Id).ToListAsync(ct)).ToHashSet();
        var snapshots = (await db.Set<ClashSnapshot>().AsNoTracking().Select(s => s.Id).ToListAsync(ct)).ToHashSet();
        foreach (var folder in Directory.EnumerateDirectories(storage.Root))
        {
            var isUploads = Path.GetFileName(folder) == "uploads";
            if (!isUploads && !Guid.TryParseExact(Path.GetFileName(folder), "N", out _)) continue;

            foreach (var child in Directory.EnumerateDirectories(folder))
            {
                if (!Guid.TryParseExact(Path.GetFileName(child), "N", out var id)) continue;
                if ((isUploads ? uploads : snapshots).Contains(id)) continue;
                if (Directory.GetLastWriteTimeUtc(child) >= cutoff.UtcDateTime) continue;

                Directory.Delete(child, recursive: true);
                logger.LogInformation("Удалена папка без записи в базе: {Folder}", child);
            }
        }
    }
}
