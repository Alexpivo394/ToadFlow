using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Alexpivo394.Revit.Clash.Render;
using Microsoft.Extensions.Options;
using ToadFlow.Contracts.Clash;
using ToadFlow.Server.Clash.Collisions;
using ToadFlow.Server.Clash.Parts;

namespace ToadFlow.Server.Clash.Images;

/// <summary>Что нужно для картинки коллизии: пара элементов и точка, прогон, последним нашедший коллизию, и модели этого прогона.</summary>
/// <param name="Models">Модели прогона со слепками; в версию не входят — их определяет прогон.</param>
public sealed record ImageTarget(
    Guid CollisionId,
    Guid PartId,
    Guid RunId,
    string ModelA,
    string UniqueIdA,
    string ModelB,
    string UniqueIdB,
    double X,
    double Y,
    double Z,
    IReadOnlyList<EngineRunModel> Models)
{
    /// <summary>
    ///     Версия картинки — адрес файла в кеше и <c>?v=</c> в адресе картинки. Новый прогон, объединение или правка рисовальщика
    ///     дают новую версию.
    /// </summary>
    public string Version => VersionOf(RunId, ModelA, UniqueIdA, ModelB, UniqueIdB, X, Y, Z);

    /// <summary>Версия картинки коллизии по её строке, без загрузки моделей прогона.</summary>
    public static string VersionOf(ClashCollision c) => VersionOf(c.LastFoundRunId, c.ModelA, c.UniqueIdA, c.ModelB, c.UniqueIdB, c.X, c.Y, c.Z);

    /// <summary>Первые 16 hex SHA-256 строки <c>"{рисовальщик}|{прогон:N}|{A}|{UniqueId A}|{B}|{UniqueId B}|{X:R}|{Y:R}|{Z:R}"</c>.</summary>
    private static string VersionOf(Guid runId, string modelA, string uniqueIdA, string modelB, string uniqueIdB, double x, double y, double z)
    {
        var text = string.Create(CultureInfo.InvariantCulture,
            $"{CollisionImages.RendererVersion}|{runId:N}|{modelA}|{uniqueIdA}|{modelB}|{uniqueIdB}|{x:R}|{y:R}|{z:R}");
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)), 0, 8);
    }
}

/// <summary>Итог запроса картинки.</summary>
/// <param name="Status">
///     200 — <paramref name="Path" /> есть; 404 — элемента нет в выгрузке; 410 — выгрузка удалена и кеша нет; 500 — отрисовка не
///     удалась (причина в журнале).
/// </param>
/// <param name="Error">Причина для ответа, если картинки нет.</param>
public sealed record ImageResult(int Status, string? Path, string? Error);

/// <summary>Предел одновременных отрисовок: <c>Clash:ImageThreads</c>; 0 — половина ядер, не меньше одного.</summary>
public sealed class CollisionImageOptions
{
    public int Threads { get; set; }

    /// <summary>Сколько интерактивных отрисовок может ждать в очереди; дальше — 429.</summary>
    public int InteractiveCapacity { get; set; } = 64;

    internal int ResolvedThreads => Threads > 0 ? Threads : Math.Max(1, Environment.ProcessorCount / 2);
}

/// <summary>
///     Картинки коллизий по запросу: файл версии из кеша (<see cref="ClashStorage.ImagePath(string, Guid, Guid, string)" />) или
///     отрисовка по слепкам прогона. Запросы одной картинки ждут одну отрисовку; она не зависит от отмены отдельного запроса.
///     Одновременно рисуется не больше <see cref="CollisionImageOptions.Threads" /> картинок. Ошибка не кешируется, а пишется в журнал
///     и отдаётся итогом 500 — исключение из отрисовки не выходит. Отрисовка
///     событий не публикует: картинка — не изменение коллизии.
/// </summary>
public sealed class CollisionImages
{
    /// <summary>Версия рисовальщика: увеличить, когда картинки должны перерисоваться (другой ракурс, цвета, размер).</summary>
    public const int RendererVersion = 2;

    private readonly RenderQueue<(Guid, string), ImageResult> _queue;
    private readonly PartElementsCache _cache;
    private readonly string _root;
    private readonly ILogger<CollisionImages> _logger;
    private int _renders;
    private int _renderStarts;
    private int _active;
    private int _maxActive;

    public CollisionImages(IOptions<ClashStorageOptions> storage, PartElementsCache cache, IOptions<CollisionImageOptions> options, ILogger<CollisionImages> logger)
    {
        _root = storage.Value.Root;
        _cache = cache;
        _logger = logger;
        Threads = options.Value.ResolvedThreads;
        ArchiveWindow = Math.Max(1, Threads / 2);
        _queue = new RenderQueue<(Guid, string), ImageResult>(Threads, ArchiveWindow, options.Value.InteractiveCapacity);
    }

    /// <summary>Сколько картинок рисуется одновременно.</summary>
    public int Threads { get; }

    /// <summary>Сколько из них могут занять выгрузки (отчёт, архив): остальные — людям.</summary>
    public int ArchiveWindow { get; }

    /// <summary>Сколько раз рисовал (для тестов).</summary>
    public int Renders => Volatile.Read(ref _renders);

    /// <summary>Сколько общих отрисовок начиналось, включая неудачные (для тестов).</summary>
    internal int RenderStarts => Volatile.Read(ref _renderStarts);

    /// <summary>Наибольшее число одновременных отрисовок (для тестов).</summary>
    internal int MaxConcurrentRenders => Volatile.Read(ref _maxActive);

    /// <param name="ct">Отменяет только ожидание этого запроса; отрисовку, которую ждут другие, он не отменяет.</param>
    /// <param name="priority">Выгрузки (отчёт, архив) рисуются в своём окне потоков и уступают людям.</param>
    /// <returns>Итог; при полной очереди интерактивных — 429 (клиент повторит через несколько секунд).</returns>
    public async Task<ImageResult> GetAsync(ImageTarget target, CancellationToken ct, ImagePriority priority = ImagePriority.Interactive)
    {
        var path = ClashStorage.ImagePath(_root, target.PartId, target.CollisionId, target.Version);
        if (File.Exists(path)) return Ok(path);

        try
        {
            return await _queue.RunAsync((target.CollisionId, target.Version), priority, token => RenderOnceAsync(target, path, token), ct);
        }
        catch (RenderQueueFullException full)
        {
            return new ImageResult(StatusCodes.Status429TooManyRequests, null, full.Message);
        }
    }

    /// <summary>Отрисовка ключа; ошибка не кешируется — следующий запрос начнёт заново или найдёт файл.</summary>
    private async Task<ImageResult> RenderOnceAsync(ImageTarget target, string path, CancellationToken ct)
    {
        Interlocked.Increment(ref _renderStarts);
        try
        {
            return await RenderAsync(target, path, ct);
        }
        catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
        {
            // Слепок удалили, пока картинка рисовалась.
            return Gone;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _logger.LogError(exception, "Картинка коллизии {Collision} (прогон {Run}) не нарисована", target.CollisionId, target.RunId);
            return new ImageResult(StatusCodes.Status500InternalServerError, null, "картинку не удалось нарисовать");
        }
    }

    /// <param name="ct">Все ждущие ушли: отрисовка бросается на ближайшей проверке (сцена ещё не собрана или ещё не нарисована).</param>
    private async Task<ImageResult> RenderAsync(ImageTarget target, string path, CancellationToken ct)
    {
        {
            // Файл мог появиться, пока запрос стоял в очереди: прежняя отрисовка закончилась и сняла ключ.
            if (File.Exists(path)) return Ok(path);
            ct.ThrowIfCancellationRequested();

            var models = new List<SceneModel>(target.Models.Count);
            foreach (var model in target.Models)
            {
                var folder = Path.Combine(_root, model.ModelId.ToString("N"), model.SnapshotId.ToString("N"));
                var geometry = Path.Combine(folder, "model.tcg");
                if (!File.Exists(geometry)) return Gone;
                models.Add(new SceneModel(_cache.Get(model.SnapshotId, folder).Index, () => File.OpenRead(geometry)));
            }

            var sceneTarget = new SceneTarget(target.ModelA, target.UniqueIdA, target.ModelB, target.UniqueIdB, target.X, target.Y, target.Z);
            byte[]? png;
            var active = Interlocked.Increment(ref _active);
            try
            {
                UpdateMax(active);
                png = await Task.Run<byte[]?>(() =>
                {
                    RenderScene scene;
                    try
                    {
                        scene = CollisionScenes.Build(models, sceneTarget);
                    }
                    catch (KeyNotFoundException)
                    {
                        return null;
                    }

                    ct.ThrowIfCancellationRequested();
                    return CollisionRenderer.Render(scene).Png;
                }, ct);
            }
            finally
            {
                Interlocked.Decrement(ref _active);
            }

            if (png is null) return new ImageResult(StatusCodes.Status404NotFound, null, MissingElement(models, target));

            Interlocked.Increment(ref _renders);
            await ClashStorage.WriteImageAsync(_root, target.PartId, target.CollisionId, target.Version, new MemoryStream(png), CancellationToken.None);
            return Ok(path);
        }
    }

    /// <summary>Какого элемента пары нет в выгрузке — человеческим текстом, с именем модели вместо её ключа.</summary>
    private static string MissingElement(IReadOnlyList<SceneModel> models, ImageTarget target)
    {
        var a = models.Any(m => m.Index.Header.ModelKey == target.ModelA && m.Index.Elements.Any(e => e.Info.UniqueId == target.UniqueIdA));
        var (modelKey, uniqueId) = a ? (target.ModelB, target.UniqueIdB) : (target.ModelA, target.UniqueIdA);
        var name = target.Models.FirstOrDefault(m => m.ModelId.ToString("N") == modelKey)?.ModelName ?? modelKey;
        return $"Элемент {uniqueId} не найден в выгрузке модели {name}";
    }

    private void UpdateMax(int active)
    {
        var seen = Volatile.Read(ref _maxActive);
        while (active > seen)
        {
            var previous = Interlocked.CompareExchange(ref _maxActive, active, seen);
            if (previous == seen) return;
            seen = previous;
        }
    }

    private static ImageResult Ok(string path) => new(StatusCodes.Status200OK, path, null);

    private static ImageResult Gone => new(StatusCodes.Status410Gone, null, "выгрузка модели удалена: картинку этого прогона уже не нарисовать");
}
