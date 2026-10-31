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
/// </summary>
public sealed class PartElementsCache : IDisposable
{
    /// <summary>Сколько элементов держать в памяти — около 200 МБ на крупных моделях.</summary>
    public const long ElementLimit = 2_000_000;

    private static readonly TimeSpan Sliding = TimeSpan.FromMinutes(10);

    private readonly MemoryCache _cache = new(new MemoryCacheOptions { SizeLimit = ElementLimit });
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
        using (var values = File.OpenRead(Path.Combine(folder, "model.tcp"))) parameters = ModelParametersFile.Read(values);
        var snapshot = new CachedSnapshot(index, parameters);
        _cache.Set(snapshotId, snapshot, new MemoryCacheEntryOptions { Size = Math.Max(1, index.Elements.Count), SlidingExpiration = Sliding });
        return snapshot;
    }

    public void Dispose() => _cache.Dispose();
}

/// <summary>Элементы последних слепков моделей части с их разделами — вход предпросмотра наборов.</summary>
public sealed class PartElements(PartModels models, ClashStorage storage, PartElementsCache cache)
{
    /// <summary>Модели без слепка или без раздела пропускаются.</summary>
    public async Task<IReadOnlyList<ElementsInput>> LoadAsync(Guid partId, CancellationToken ct)
    {
        var result = new List<ElementsInput>();
        foreach (var model in await models.OfPartAsync(partId, ct))
        {
            if (model is not { Discipline: { } discipline, Latest: { } latest }) continue;
            var snapshot = cache.Get(latest.Id, storage.SnapshotFolder(model.ModelId, latest.Id));
            result.Add(new ElementsInput(discipline, snapshot.Index.Header.ModelKey, snapshot.Elements, snapshot.Parameters));
        }

        return result;
    }
}
