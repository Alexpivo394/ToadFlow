using Alexpivo394.Revit.Clash.Geometry;
using Alexpivo394.Revit.Clash.Rules;
using Microsoft.Extensions.Caching.Memory;

namespace ToadFlow.Server.Clash.Parts;

/// <summary>
///     Разобранные слепки для предпросмотра наборов: описания элементов из оглавления TCG и параметры из TCP, без сеток.
///     Редактор спрашивает предпросмотр на каждое изменение условия, поэтому слепок держится в памяти, пока им пользуются.
/// </summary>
public sealed class PartElementsCache : IDisposable
{
    /// <summary>Сколько элементов держать в памяти — около 200 МБ на крупных моделях.</summary>
    public const long ElementLimit = 2_000_000;

    private static readonly TimeSpan Sliding = TimeSpan.FromMinutes(10);

    private readonly MemoryCache _cache = new(new MemoryCacheOptions { SizeLimit = ElementLimit });
    private int _loads;

    /// <summary>Сколько раз слепок читался с диска (для тестов).</summary>
    public int Loads => Volatile.Read(ref _loads);

    public (IReadOnlyList<ElementInfo> Elements, ModelParameters Parameters, string ModelKey) Get(Guid snapshotId, string folder) =>
        _cache.GetOrCreate(snapshotId, entry =>
        {
            Interlocked.Increment(ref _loads);
            ModelIndex index;
            using (var geometry = File.OpenRead(Path.Combine(folder, "model.tcg"))) index = ModelGeometryFile.ReadIndex(geometry);
            ModelParameters parameters;
            using (var values = File.OpenRead(Path.Combine(folder, "model.tcp"))) parameters = ModelParametersFile.Read(values);
            entry.Size = Math.Max(1, index.Elements.Count);
            entry.SlidingExpiration = Sliding;
            return ((IReadOnlyList<ElementInfo>)index.Elements.Select(e => e.Info).ToArray(), parameters, index.Header.ModelKey);
        });

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
            var (elements, parameters, key) = cache.Get(latest.Id, storage.SnapshotFolder(model.ModelId, latest.Id));
            result.Add(new ElementsInput(discipline, key, elements, parameters));
        }

        return result;
    }
}
