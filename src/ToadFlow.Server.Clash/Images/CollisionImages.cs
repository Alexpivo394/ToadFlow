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
/// <param name="Status">200 — <paramref name="Path" /> есть; 404 — элемента нет в выгрузке; 410 — выгрузка удалена и кеша нет.</param>
/// <param name="Error">Причина для ответа, если картинки нет.</param>
public sealed record ImageResult(int Status, string? Path, string? Error);

/// <summary>Предел одновременных отрисовок: <c>Clash:ImageThreads</c>; 0 — половина ядер, не меньше одного.</summary>
public sealed class CollisionImageOptions
{
    public int Threads { get; set; }

    internal int ResolvedThreads => Threads > 0 ? Threads : Math.Max(1, Environment.ProcessorCount / 2);
}

/// <summary>
///     Картинки коллизий по запросу: файл версии из кеша (<see cref="ClashStorage.ImagePath(string, Guid, Guid, string)" />) или
///     отрисовка по слепкам прогона. Запросы одной картинки ждут одну отрисовку; она не зависит от отмены отдельного запроса.
///     Одновременно рисуется не больше <see cref="CollisionImageOptions.Threads" /> картинок. Ошибка не кешируется. Отрисовка
///     событий не публикует: картинка — не изменение коллизии.
/// </summary>
public sealed class CollisionImages
{
    /// <summary>Версия рисовальщика: увеличить, когда картинки должны перерисоваться (другой ракурс, цвета, размер).</summary>
    public const int RendererVersion = 1;

    private readonly ConcurrentDictionary<(Guid, string), Lazy<Task<ImageResult>>> _pending = new();
    private readonly PartElementsCache _cache;
    private readonly SemaphoreSlim _gate;
    private readonly string _root;
    private int _renders;
    private int _active;
    private int _maxActive;

    public CollisionImages(IOptions<ClashStorageOptions> storage, PartElementsCache cache, IOptions<CollisionImageOptions> options)
    {
        _root = storage.Value.Root;
        _cache = cache;
        var threads = options.Value.ResolvedThreads;
        _gate = new SemaphoreSlim(threads, threads);
    }

    /// <summary>Сколько раз рисовал (для тестов).</summary>
    public int Renders => Volatile.Read(ref _renders);

    /// <summary>Наибольшее число одновременных отрисовок (для тестов).</summary>
    internal int MaxConcurrentRenders => Volatile.Read(ref _maxActive);

    /// <param name="ct">Отменяет только ожидание этого запроса; отрисовку, которую ждут другие, он не отменяет.</param>
    public async Task<ImageResult> GetAsync(ImageTarget target, CancellationToken ct)
    {
        var path = ClashStorage.ImagePath(_root, target.PartId, target.CollisionId, target.Version);
        if (File.Exists(path)) return Ok(path);

        var key = (target.CollisionId, target.Version);
        var render = _pending.GetOrAdd(key, _ => new Lazy<Task<ImageResult>>(() => Task.Run(() => RenderOnceAsync(key, target, path))));
        return await render.Value.WaitAsync(ct);
    }

    /// <summary>Общая отрисовка ключа; по завершении — и при ошибке — ключ снимается, следующий запрос начнёт заново или найдёт файл.</summary>
    private async Task<ImageResult> RenderOnceAsync((Guid, string) key, ImageTarget target, string path)
    {
        try
        {
            return await RenderAsync(target, path);
        }
        finally
        {
            _pending.TryRemove(key, out _);
        }
    }

    private async Task<ImageResult> RenderAsync(ImageTarget target, string path)
    {
        await _gate.WaitAsync();
        try
        {
            // Файл мог появиться, пока запрос шёл к общей отрисовке: прежняя закончилась и сняла ключ.
            if (File.Exists(path)) return Ok(path);

            var models = new List<SceneModel>(target.Models.Count);
            foreach (var model in target.Models)
            {
                var folder = Path.Combine(_root, model.ModelId.ToString("N"), model.SnapshotId.ToString("N"));
                var geometry = Path.Combine(folder, "model.tcg");
                if (!File.Exists(geometry))
                    return new ImageResult(StatusCodes.Status410Gone, null, "выгрузка модели удалена: картинку этого прогона уже не нарисовать");
                models.Add(new SceneModel(_cache.Get(model.SnapshotId, folder).Index, () => File.OpenRead(geometry)));
            }

            var scene = new SceneTarget(target.ModelA, target.UniqueIdA, target.ModelB, target.UniqueIdB, target.X, target.Y, target.Z);
            byte[] png;
            var active = Interlocked.Increment(ref _active);
            try
            {
                UpdateMax(active);
                png = await Task.Run(() => CollisionRenderer.Render(CollisionScenes.Build(models, scene)).Png);
            }
            catch (KeyNotFoundException exception)
            {
                return new ImageResult(StatusCodes.Status404NotFound, null, exception.Message);
            }
            finally
            {
                Interlocked.Decrement(ref _active);
            }

            Interlocked.Increment(ref _renders);
            await ClashStorage.WriteImageAsync(_root, target.PartId, target.CollisionId, target.Version, new MemoryStream(png), CancellationToken.None);
            return Ok(path);
        }
        finally
        {
            _gate.Release();
        }
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
}
