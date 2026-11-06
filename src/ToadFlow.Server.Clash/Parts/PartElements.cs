using System.Collections.Concurrent;
using Alexpivo394.Revit.Clash.Geometry;
using Alexpivo394.Revit.Clash.Rules;
using Microsoft.Extensions.Caching.Memory;

namespace ToadFlow.Server.Clash.Parts;

/// <summary>Разобранный слепок без сеток: оглавление TCG (габариты и смещения сеток) и параметры TCP.</summary>
public sealed record CachedSnapshot(ModelIndex Index, ModelParameters Parameters)
{
    public IReadOnlyList<ElementInfo> Elements { get; } = Index.Elements.Select(e => e.Info).ToArray();
}

/// <summary>
///     Разобранные слепки для предпросмотра наборов и просмотрщика: оглавление TCG и параметры TCP, без сеток.
///     Редактор спрашивает предпросмотр на каждое изменение условия, поэтому слепок держится в памяти, пока им пользуются.
///     Предел — по оценке занятой памяти, а не по числу элементов: опись и параметры весят больше элементов.
/// </summary>
/// <param name="byteLimit">Сколько памяти держать под слепки, байт; слепок больше предела отдаётся, но не запоминается.</param>
public sealed class PartElementsCache(long byteLimit = PartElementsCache.DefaultByteLimit) : IDisposable
{
    /// <summary>Предел, если память процесса неизвестна, — 512 МБ.</summary>
    public const long DefaultByteLimit = 512L << 20;

    /// <summary>
    ///     Предел кеша при запуске: <c>Clash:ElementsCacheMb</c>, если задан, иначе половина памяти, доступной сборщику мусора
    ///     (в контейнере — от его лимита). Остальное — загрузкам, картинкам и запросам.
    /// </summary>
    public static long LimitFor(int? configuredMb, long availableBytes) =>
        configuredMb is > 0 ? (long)configuredMb.Value << 20
        : availableBytes > 0 ? Math.Max(DefaultByteLimit, availableBytes / 2)
        : DefaultByteLimit;

    // Замер на 10 реальных моделях: оглавление — 0,9–1,9 КБ на элемент без описи, запись описи — около 130 Б,
    // разобранные параметры — в 3,7–6,5 раза больше файла. Множители взяты с запасом.
    private const long BytesPerElement = 2_000;
    private const long BytesPerInventoryEntry = 160;
    private const long BytesPerParametersFileByte = 8;

    private static readonly TimeSpan Sliding = TimeSpan.FromMinutes(10);

    private readonly MemoryCache _cache = new(new MemoryCacheOptions { SizeLimit = byteLimit });
    private readonly ConcurrentDictionary<Guid, Lazy<CachedSnapshot>> _loading = new();
    private int _loads;

    /// <summary>Сколько раз слепок читался с диска (для тестов).</summary>
    public int Loads => Volatile.Read(ref _loads);

    /// <summary>
    ///     Слепок из памяти или с диска. Одновременные запросы холодного слепка ждут одно чтение; неудачное чтение не запоминается —
    ///     следующий запрос прочтёт заново.
    /// </summary>
    public CachedSnapshot Get(Guid snapshotId, string folder)
    {
        if (_cache.TryGetValue(snapshotId, out CachedSnapshot? cached)) return cached!;
        var load = _loading.GetOrAdd(snapshotId, id => new Lazy<CachedSnapshot>(() => Load(id, folder)));
        try
        {
            return load.Value;
        }
        finally
        {
            _loading.TryRemove(KeyValuePair.Create(snapshotId, load));
        }
    }

    /// <summary>Читает слепок и кладёт в память; слепок, положенный между проверкой и чтением, берётся из памяти.</summary>
    private CachedSnapshot Load(Guid snapshotId, string folder)
    {
        if (_cache.TryGetValue(snapshotId, out CachedSnapshot? cached)) return cached!;
        Interlocked.Increment(ref _loads);
        ModelIndex index;
        using (var geometry = File.OpenRead(Path.Combine(folder, "model.tcg"))) index = ModelGeometryFile.ReadIndex(geometry);
        ModelParameters parameters;
        long parametersFileBytes;
        using (var values = File.OpenRead(Path.Combine(folder, "model.tcp")))
        {
            parametersFileBytes = values.Length;
            parameters = ModelParametersFile.Read(values);
        }

        var snapshot = new CachedSnapshot(index, parameters);
        _cache.Set(snapshotId, snapshot, new MemoryCacheEntryOptions { Size = EstimatedBytes(index, parametersFileBytes), SlidingExpiration = Sliding });
        return snapshot;
    }

    /// <summary>Оценка памяти разобранного слепка, байт: элементы оглавления, опись и параметры по размеру файла.</summary>
    public static long EstimatedBytes(ModelIndex index, long parametersFileBytes) =>
        Math.Max(1, index.Elements.Count * BytesPerElement + index.Inventory.Count * BytesPerInventoryEntry
                    + parametersFileBytes * BytesPerParametersFileByte);

    public void Dispose() => _cache.Dispose();
}

/// <summary>Элементы последних слепков моделей части с их разделами — вход предпросмотра наборов.</summary>
public sealed class PartElements(PartModels models, ClashStorage storage, PartElementsCache cache)
{
    /// <summary>
    ///     Модели источников заданных групп: набор ОВ не читает слепки АР и КР. Модели без слепка или без раздела пропускаются;
    ///     группы, которой нет в черновике, моделей не даёт.
    /// </summary>
    public Task<IReadOnlyList<ElementsInput>> LoadAsync(Guid partId, ClashConfiguration config, IEnumerable<Guid> groupIds, CancellationToken ct)
    {
        var wanted = groupIds.ToHashSet();
        var groups = config.Groups.Where(g => wanted.Contains(g.Id)).ToList();
        return LoadAsync(partId, groups, ct);
    }

    // Ключ модели в слепке — Id модели в форме N (SnapshotPublisher это проверяет), поэтому отбор идёт до чтения слепка.
    private async Task<IReadOnlyList<ElementsInput>> LoadAsync(Guid partId, IReadOnlyList<SetGroup> groups, CancellationToken ct)
    {
        var result = new List<ElementsInput>();
        if (groups.Count == 0) return result;
        foreach (var model in await models.OfPartAsync(partId, ct))
        {
            if (model is not { Discipline: { } discipline, Latest: { } latest }) continue;
            if (!groups.Any(g => g.Source.Includes(discipline, model.ModelId.ToString("N")))) continue;
            var snapshot = cache.Get(latest.Id, storage.SnapshotFolder(model.ModelId, latest.Id));
            result.Add(new ElementsInput(discipline, snapshot.Index.Header.ModelKey, snapshot.Elements, snapshot.Parameters));
        }

        return result;
    }
}
