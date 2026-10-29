using Alexpivo394.Revit.Clash.Engine;
using Alexpivo394.Revit.Clash.Engine.Identity;
using Alexpivo394.Revit.Clash.Geometry;
using Alexpivo394.Revit.Clash.Rules;
using ToadFlow.Contracts.Clash;

namespace ToadFlow.Clash.Worker;

/// <summary>Прошлая выгрузка модели для утверждённого прогона: оглавление и параметры, без сеток.</summary>
/// <param name="Current">Слепок тот же, что в нынешнем прогоне.</param>
public sealed record PreviousModel(EngineRunModel Model, ModelIndex Index, ModelParameters Parameters, bool Current);

/// <summary>
///     Факты утверждённого прогона для сервера: основа инкрементальности, ушедшие открытые пары и предложения сопоставить
///     пересозданные элементы. Статусы по ним выбирает сервер.
/// </summary>
public static class ApprovedRunFacts
{
    /// <summary>Одна ли это конфигурация: сравнение в каноническом JSON.</summary>
    public static bool SameConfiguration(string a, string b) =>
        ConfigurationJson.Serialize(ConfigurationJson.Deserialize(a)) == ConfigurationJson.Serialize(ConfigurationJson.Deserialize(b));

    /// <summary>Основа для движка: состояния элементов прошлых выгрузок и коллизии прошлого прогона.</summary>
    public static IncrementalBase Base(IReadOnlyList<PreviousModel> previous, ClashConfiguration config, IReadOnlyList<EngineOpenPair> open)
    {
        var inputs = previous.Select(p => new ElementsInput(p.Model.Discipline, p.Index.Header.ModelKey, p.Index.Elements.Select(e => e.Info).ToArray(),
            p.Parameters)).ToList();
        var fingerprints = new Dictionary<ElementRef, ulong>();
        foreach (var model in previous)
        foreach (var entry in model.Index.Elements)
            fingerprints[new ElementRef(model.Index.Header.ModelKey, entry.Info.UniqueId)] = entry.Fingerprint;
        var hits = open.Where(p => p.FoundByPrevious)
            .Select(p => new ClashHit(new ElementRef(p.ModelA, p.UniqueIdA), new ElementRef(p.ModelB, p.UniqueIdB), p.X, p.Y, p.Z, p.DepthMm, []))
            .ToList();
        return IncrementalBase.From(inputs, fingerprints, config, hits);
    }

    /// <summary>
    ///     Открытые пары основы, которых нет среди найденных: где сейчас каждый элемент и, если конфигурация сменилась, нашла бы
    ///     пару прошлая конфигурация на нынешней геометрии. Элемент модели, которой больше нет в прогоне, — «скрыт».
    /// </summary>
    /// <param name="previousConfig">Конфигурация прошлого прогона, если она отличается от нынешней; иначе null.</param>
    public static List<EngineGonePair> Gone(IReadOnlyList<EngineOpenPair> open, IReadOnlyList<ClashHit> found, IReadOnlyList<ModelInput> models,
        ClashConfiguration? previousConfig, int threads, CancellationToken ct)
    {
        var foundPairs = found.Select(h => (h.A, h.B)).ToHashSet();
        var geometry = new HashSet<ElementRef>();
        var inventory = new HashSet<ElementRef>();
        var modelKeys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var model in models)
        {
            var key = model.Geometry.Header.ModelKey;
            modelKeys.Add(key);
            foreach (var element in model.Geometry.Elements) geometry.Add(new ElementRef(key, element.Info.UniqueId));
            foreach (var uniqueId in model.Geometry.Inventory) inventory.Add(new ElementRef(key, uniqueId));
        }

        string Presence(ElementRef element) =>
            geometry.Contains(element) ? "InGeometry" : inventory.Contains(element) || !modelKeys.Contains(element.ModelKey) ? "InventoryOnly" : "Absent";

        var missing = open.Select(p => (A: new ElementRef(p.ModelA, p.UniqueIdA), B: new ElementRef(p.ModelB, p.UniqueIdB)))
            .Where(p => !foundPairs.Contains(p)).ToList();
        HashSet<(ElementRef, ElementRef)>? wouldHit = null;
        if (previousConfig is not null)
        {
            var recheck = missing.Where(p => geometry.Contains(p.A) && geometry.Contains(p.B)).ToList();
            wouldHit = new ClashEngine(new EngineOptions(threads)).Recheck(models, previousConfig, recheck, ct).Select(h => (h.A, h.B)).ToHashSet();
        }

        return missing.Select(p =>
        {
            var (a, b) = (Presence(p.A), Presence(p.B));
            bool? hit = wouldHit is not null && a == "InGeometry" && b == "InGeometry" ? wouldHit.Contains(p) : null;
            return new EngineGonePair(p.A.ModelKey, p.A.UniqueId, p.B.ModelKey, p.B.UniqueId, a, b, hit);
        }).ToList();
    }

    /// <summary>
    ///     Предложения сопоставить пересозданные элементы моделей с новой выгрузкой: только те, где новый элемент — в найденной
    ///     коллизии, а старый — в открытой.
    /// </summary>
    public static List<EngineIdentityMatch> Matches(IReadOnlyList<PreviousModel> previous, IReadOnlyDictionary<string, ModelIndex> current,
        IReadOnlyList<ClashHit> found, IReadOnlyList<EngineOpenPair> open)
    {
        var inHits = found.SelectMany(h => new[] { h.A, h.B }).ToHashSet();
        var inOpen = open.SelectMany(p => new[] { new ElementRef(p.ModelA, p.UniqueIdA), new ElementRef(p.ModelB, p.UniqueIdB) }).ToHashSet();
        var result = new List<EngineIdentityMatch>();
        foreach (var model in previous.Where(p => !p.Current))
        {
            var key = model.Index.Header.ModelKey;
            if (!current.TryGetValue(key, out var index)) continue;
            foreach (var match in ElementMatcher.Compare(model.Index, index).FallbackMatches)
            {
                if (inHits.Contains(new ElementRef(key, match.NewId)) && inOpen.Contains(new ElementRef(key, match.OldId)))
                    result.Add(new EngineIdentityMatch(key, match.OldId, match.NewId));
            }
        }

        return result;
    }
}
