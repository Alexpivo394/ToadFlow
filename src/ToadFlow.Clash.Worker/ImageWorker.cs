using System.Collections.Concurrent;
using System.Diagnostics;
using Alexpivo394.Revit.Clash.Geometry;
using Alexpivo394.Revit.Clash.Render;
using Microsoft.Extensions.Logging;
using ToadFlow.Contracts.Clash;

namespace ToadFlow.Clash.Worker;

/// <summary>
///     Картинки коллизий одной части: скачать слепки прогона (кеш), построить индекс сцены один раз на задание и рисовать
///     пачками на <see cref="WorkerOptions.Threads" /> потоках. Между пачками работник уступает задание прогону движка, если тот
///     ждёт: картинки не задерживают поиск.
/// </summary>
public sealed class ImageWorker(EngineClient client, SnapshotCache cache, WorkerOptions options, ILogger<ImageWorker> logger)
{
    /// <returns>null — заданий нет; true — задание закончено; false — брошено (переключено на другой прогон, уступлено) или сбой.</returns>
    public async Task<bool?> RunOnceAsync(CancellationToken ct)
    {
        var assignment = await client.ClaimImagesAsync(ct);
        if (assignment is null) return null;

        var (partId, runId) = (assignment.PartId, assignment.RunId);
        var watch = Stopwatch.StartNew();
        var drawn = 0;
        try
        {
            var models = new List<ModelGeometry>(assignment.Models.Count);
            foreach (var model in assignment.Models)
            {
                var folder = await cache.GetAsync(model, ct);
                await using var stream = File.OpenRead(Path.Combine(folder, SnapshotCache.Geometry));
                models.Add(ModelGeometryFile.Read(stream));
            }

            cache.Trim(assignment.Models.Select(m => m.SnapshotId).ToList());
            var index = new CollisionSceneIndex(models);
            logger.LogInformation("Картинки части {Part} (прогон {Run}): модели загружены за {Load:0.0} с", partId, runId, watch.Elapsed.TotalSeconds);

            while (true)
            {
                var batch = await client.ImageBatchAsync(partId, runId, ct);
                if (batch is null) return Dropped(partId, runId);
                if (batch.EngineRunWaiting)
                {
                    await client.ImagesFailAsync(partId, runId, "уступлено прогону движка", yield: true, ct);
                    logger.LogInformation("Картинки части {Part}: уступлено прогону движка, нарисовано {Drawn}", partId, drawn);
                    return false;
                }

                if (batch.Items.Count == 0)
                {
                    if (!await client.ImagesDoneAsync(partId, runId, ct)) return Dropped(partId, runId);
                    logger.LogInformation("Картинки части {Part}: {Drawn} за {Seconds:0.0} с", partId, drawn, watch.Elapsed.TotalSeconds);
                    return true;
                }

                var results = Render(index, batch.Items, ct);
                if (!await client.PostImagesAsync(partId, runId, results, ct)) return Dropped(partId, runId);
                drawn += results.Count(r => r.Png is not null);
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            logger.LogError(exception, "Картинки части {Part} не нарисованы", partId);
            try
            {
                await client.ImagesFailAsync(partId, runId, exception.Message, yield: false, ct);
            }
            catch (HttpRequestException report)
            {
                logger.LogWarning(report, "Сбой картинок части {Part} не дошёл до сервера — задание вернёт истёкшая аренда", partId);
            }

            return false;
        }
    }

    /// <summary>Рисует пачку параллельно; коллизия, которую нарисовать не вышло (элемента нет в слепке, битая геометрия), сдаётся как «без картинки», а пачка идёт дальше.</summary>
    private List<(EngineImageResult Result, byte[]? Png)> Render(CollisionSceneIndex index, IReadOnlyList<EngineImageRequest> items, CancellationToken ct)
    {
        var results = new (EngineImageResult Result, byte[]? Png)[items.Count];
        var parallel = new ParallelOptions { CancellationToken = ct, MaxDegreeOfParallelism = options.Threads > 0 ? options.Threads : -1 };
        var failures = new ConcurrentBag<string>();
        Parallel.For(0, items.Count, parallel, i =>
        {
            var item = items[i];
            byte[]? png = null;
            try
            {
                var scene = index.Collect(item.ModelA, item.UniqueIdA, item.ModelB, item.UniqueIdB, item.X, item.Y, item.Z);
                png = CollisionRenderer.Render(scene).Png;
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                failures.Add($"{item.CollisionId}: {exception.Message}");
            }

            results[i] = (new EngineImageResult(item.CollisionId, item.FingerprintA, item.FingerprintB, png is not null), png);
        });
        if (!failures.IsEmpty) logger.LogWarning("Без картинки {Count}: {First}", failures.Count, failures.First());
        return results.ToList();
    }

    private bool Dropped(Guid partId, Guid runId)
    {
        logger.LogInformation("Картинки части {Part}: задание прогона {Run} переключено на новый прогон — брошено", partId, runId);
        return false;
    }
}
