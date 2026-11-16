using System.Collections.Concurrent;
using System.Runtime.ExceptionServices;
using Alexpivo394.Revit.Clash.Geometry;
using Alexpivo394.Revit.Clash.Rules;

namespace ToadFlow.Server.Clash.Parts;

/// <summary>Разобранный слепок без сеток: оглавление TCG (габариты и смещения сеток) и параметры TCP.</summary>
public sealed record CachedSnapshot(ModelIndex Index, IReadOnlyList<ElementInfo> Elements, ModelParameters Parameters);

/// <summary>
///     Разобранные слепки для предпросмотра наборов, просмотрщика и картинок: оглавление TCG и параметры TCP, без сеток.
///     Редактор спрашивает предпросмотр на каждое изменение условия, поэтому слепок держится в памяти, пока им пользуются.
///     Оглавление и параметры хранятся порознь: 3D и картинкам нужно только оглавление, а параметры занимают больше всего.
///     Предел — по оценке занятой памяти, а не по числу элементов; новое вытесняет давно не нужное.
/// </summary>
/// <param name="byteLimit">Сколько памяти держать под слепки, байт; часть слепка больше предела отдаётся, но не запоминается.</param>
public sealed class PartElementsCache(long byteLimit = PartElementsCache.DefaultByteLimit, TimeProvider? time = null) : IDisposable
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

    /// <summary>Сколько держится часть слепка, к которой не обращались.</summary>
    public static readonly TimeSpan Sliding = TimeSpan.FromMinutes(10);

    /// <summary>Сколько оглавлений читается одновременно: больше упирается в диск и пиковую память.</summary>
    private const int ParallelLoads = 4;

    private sealed record IndexPart(ModelIndex Index, ElementInfo[] Elements);

    private readonly record struct Key(Guid SnapshotId, bool Parameters);

    private sealed class Entry(Key key, object value, long size)
    {
        public Key Key { get; } = key;
        public object Value { get; } = value;
        public long Size { get; } = size;
        public DateTimeOffset LastUsed { get; set; }
    }

    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private readonly Lock _lock = new();
    private readonly Dictionary<Key, LinkedListNode<Entry>> _entries = [];
    private readonly LinkedList<Entry> _recent = []; // первым — последний использованный
    private long _size;
    private readonly ConcurrentDictionary<Key, Lazy<object>> _loading = new();
    private int _loads;
    private int _parameterLoads;

    /// <summary>Сколько раз оглавление читалось с диска (для тестов).</summary>
    public int Loads => Volatile.Read(ref _loads);

    /// <summary>Сколько раз параметры читались с диска (для тестов).</summary>
    public int ParameterLoads => Volatile.Read(ref _parameterLoads);

    /// <summary>Оглавление слепка из памяти или с диска; параметры не читаются.</summary>
    public ModelIndex GetIndex(Guid snapshotId, string folder) => IndexOf(snapshotId, folder).Index;

    /// <summary>
    ///     Оглавления нескольких слепков в порядке запроса; холодные читаются параллельно. Ошибка чтения бросается как есть
    ///     (первая из случившихся), а не в <see cref="AggregateException" />.
    /// </summary>
    public IReadOnlyList<ModelIndex> GetIndexes(IReadOnlyList<(Guid SnapshotId, string Folder)> snapshots)
    {
        var result = new ModelIndex[snapshots.Count];
        try
        {
            Parallel.For(0, snapshots.Count, new ParallelOptions { MaxDegreeOfParallelism = ParallelLoads },
                i => result[i] = GetIndex(snapshots[i].SnapshotId, snapshots[i].Folder));
        }
        catch (AggregateException exception) when (exception.InnerExceptions.Count > 0)
        {
            ExceptionDispatchInfo.Throw(exception.InnerExceptions[0]);
        }

        return result;
    }

    /// <summary>
    ///     Слепок целиком из памяти или с диска. Одновременные запросы холодного слепка ждут одно чтение; неудачное чтение не
    ///     запоминается — следующий запрос прочтёт заново.
    /// </summary>
    public CachedSnapshot Get(Guid snapshotId, string folder)
    {
        var index = IndexOf(snapshotId, folder);
        var parameters = (ModelParameters)Cached(new Key(snapshotId, Parameters: true), () => LoadParameters(folder));
        return new CachedSnapshot(index.Index, index.Elements, parameters);
    }

    private IndexPart IndexOf(Guid snapshotId, string folder) =>
        (IndexPart)Cached(new Key(snapshotId, Parameters: false), () => LoadIndex(folder));

    private object Cached(Key key, Func<(object Value, long Size)> load)
    {
        if (TryGet(key, out var cached)) return cached;
        var loading = _loading.GetOrAdd(key, k => new Lazy<object>(() =>
        {
            // Положено между проверкой и чтением — берётся из памяти.
            if (TryGet(k, out var again)) return again;
            var (value, size) = load();
            Put(k, value, size);
            return value;
        }));
        try
        {
            return loading.Value;
        }
        finally
        {
            _loading.TryRemove(KeyValuePair.Create(key, loading));
        }
    }

    private (object, long) LoadIndex(string folder)
    {
        Interlocked.Increment(ref _loads);
        ModelIndex index;
        using (var geometry = File.OpenRead(Path.Combine(folder, "model.tcg"))) index = ModelGeometryFile.ReadIndex(geometry);
        return (new IndexPart(index, index.Elements.Select(e => e.Info).ToArray()), EstimatedIndexBytes(index));
    }

    private (object, long) LoadParameters(string folder)
    {
        Interlocked.Increment(ref _parameterLoads);
        using var values = File.OpenRead(Path.Combine(folder, "model.tcp"));
        var length = values.Length;
        return (ModelParametersFile.Read(values), EstimatedParametersBytes(length));
    }

    private bool TryGet(Key key, out object value)
    {
        lock (_lock)
        {
            if (_entries.TryGetValue(key, out var node))
            {
                var now = _time.GetUtcNow();
                if (now - node.Value.LastUsed <= Sliding)
                {
                    node.Value.LastUsed = now;
                    _recent.Remove(node);
                    _recent.AddFirst(node);
                    value = node.Value.Value;
                    return true;
                }

                Remove(node);
            }
        }

        value = null!;
        return false;
    }

    /// <summary>Запоминает часть слепка, вытесняя просроченные и давно не нужные; больше предела — не запоминает.</summary>
    private void Put(Key key, object value, long size)
    {
        if (size > byteLimit) return;
        lock (_lock)
        {
            if (_entries.TryGetValue(key, out var existing)) Remove(existing);
            var now = _time.GetUtcNow();
            while (_recent.Last is { } oldest && (_size + size > byteLimit || now - oldest.Value.LastUsed > Sliding)) Remove(oldest);
            _entries[key] = _recent.AddFirst(new Entry(key, value, size) { LastUsed = now });
            _size += size;
        }
    }

    private void Remove(LinkedListNode<Entry> node)
    {
        _recent.Remove(node);
        _entries.Remove(node.Value.Key);
        _size -= node.Value.Size;
    }

    /// <summary>Оценка памяти разобранного слепка, байт: элементы оглавления, опись и параметры по размеру файла.</summary>
    public static long EstimatedBytes(ModelIndex index, long parametersFileBytes) =>
        EstimatedIndexBytes(index) + EstimatedParametersBytes(parametersFileBytes);

    /// <summary>Оценка памяти разобранного оглавления, байт: элементы и опись.</summary>
    public static long EstimatedIndexBytes(ModelIndex index) =>
        Math.Max(1, index.Elements.Count * BytesPerElement + index.Inventory.Count * BytesPerInventoryEntry);

    private static long EstimatedParametersBytes(long fileBytes) => Math.Max(1, fileBytes * BytesPerParametersFileByte);

    public void Dispose()
    {
        lock (_lock)
        {
            _entries.Clear();
            _recent.Clear();
            _size = 0;
        }
    }
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
