using System.Diagnostics;
using Alexpivo394.Revit.Clash.Engine;
using Alexpivo394.Revit.Clash.Geometry;
using Alexpivo394.Revit.Clash.Rules;
using Microsoft.Extensions.Logging;
using ToadFlow.Contracts.Clash;

namespace ToadFlow.Clash.Worker;

public enum RunOutcome
{
    Succeeded,
    Failed,
    Cancelled
}

/// <summary>
///     Один прогон черновика: скачать слепки (кеш), прочитать модели, найти коллизии, дополнить их сведениями об элементах
///     и сдать частями. Ход идёт на сервер во время всего прогона — так сервер видит, что работник жив, а работник узнаёт
///     об отмене (409) и бросает поиск.
/// </summary>
public sealed class EngineWorker(EngineClient client, SnapshotCache cache, WorkerOptions options, ILogger<EngineWorker> logger)
{
    /// <returns>Итог прогона; null — очередь пуста.</returns>
    public async Task<RunOutcome?> RunOnceAsync(CancellationToken ct)
    {
        var assignment = await client.ClaimAsync(ct);
        return assignment is null ? null : await ProcessAsync(assignment, ct);
    }

    public async Task<RunOutcome> ProcessAsync(EngineRunAssignment assignment, CancellationToken ct)
    {
        var runId = assignment.RunId;
        using var cancelled = CancellationTokenSource.CreateLinkedTokenSource(ct);
        logger.LogInformation("Прогон {Run}: моделей {Models}, попытка {Attempt}", runId, assignment.Models.Count, assignment.Attempt);
        try
        {
            var load = Stopwatch.StartNew();
            var models = new List<ModelInput>(assignment.Models.Count);
            var folders = new Dictionary<Guid, string>();
            for (var i = 0; i < assignment.Models.Count; i++)
            {
                var model = assignment.Models[i];
                if (!await client.ProgressAsync(runId, "Загрузка моделей", $"{i + 1} из {assignment.Models.Count}: {model.ModelName}", ct)) return Cancelled(runId);
                folders[model.SnapshotId] = await cache.GetAsync(model, ct);
                models.Add(Read(model, folders[model.SnapshotId]));
            }

            var config = ConfigurationJson.Deserialize(assignment.ConfigurationJson);
            EngineBasis? basis = null;
            IReadOnlyList<PreviousModel> previous = [];
            ClashConfiguration? previousConfig = null;
            IncrementalBase? incremental = null;
            if (assignment.Kind == EngineApi.Approved)
            {
                basis = await client.BasisAsync(runId, ct);
                if (basis is null) return Cancelled(runId);
                if (basis.PreviousConfigurationJson is { } previousJson)
                {
                    previous = await ReadPreviousAsync(runId, basis.PreviousModels, folders, ct);
                    // Пары прошлого прогона переиспользуются, только если их искал тот же алгоритм с той же конфигурацией.
                    if (basis.PreviousEngineVersion == ClashEngine.AlgorithmVersion &&
                        ApprovedRunFacts.SameConfiguration(previousJson, assignment.ConfigurationJson))
                    {
                        // Без всех прошлых выгрузок неизменность пары не доказать — полный пересчёт.
                        if (previous.Count == basis.PreviousModels.Count) incremental = ApprovedRunFacts.Base(previous, config, basis.Open);
                    }
                    else previousConfig = ConfigurationJson.Deserialize(previousJson);
                }
            }

            load.Stop();
            cache.Trim(assignment.Models.Select(m => m.SnapshotId).Concat(previous.Select(p => p.Model.SnapshotId)).ToList());

            if (!await client.ProgressAsync(runId, "Поиск пересечений", "", ct)) return Cancelled(runId);
            var search = Task.Run(() => new ClashEngine(new EngineOptions(options.Threads)).Run(models, config, cancelled.Token, incremental), cancelled.Token);
            while (await Task.WhenAny(search, Task.Delay(options.ProgressInterval, ct)) != search)
            {
                if (await client.ProgressAsync(runId, "Поиск пересечений", "", ct)) continue;
                await cancelled.CancelAsync();
                await IgnoreCancellation(search);
                return Cancelled(runId);
            }

            var result = await search;
            if (!await client.ProgressAsync(runId, "Запись результата", $"коллизий {result.Hits.Count}", ct)) return Cancelled(runId);
            var hits = HitEnricher.Enrich(result.Hits, models, config);
            var process = Process.GetCurrentProcess();
            process.Refresh();
            var stats = new EngineRunStats(result.Stats.Elements, result.Stats.Triangles, result.Stats.CandidatePairs, load.Elapsed.TotalSeconds,
                result.Stats.BroadPhase.TotalSeconds, result.Stats.NarrowPhase.TotalSeconds, result.Stats.PeakManagedBytes, process.PeakWorkingSet64,
                result.Stats.ReusedPairs, ClashEngine.AlgorithmVersion);

            List<EngineGonePair>? gone = null;
            List<EngineIdentityMatch>? matches = null;
            if (basis is not null)
            {
                gone = ApprovedRunFacts.Gone(basis.Open, result.Hits, models, previousConfig, options.Threads, cancelled.Token);
                var current = assignment.Models.ToDictionary(m => m.ModelId.ToString("N"), m => ReadIndex(folders[m.SnapshotId]));
                matches = ApprovedRunFacts.Matches(previous, current, result.Hits, basis.Open,
                    model => logger.LogWarning("Прогон {Run}: модель {Model} слишком плотная для сопоставления пересозданных элементов — предложений нет",
                        runId, model));
            }

            var parts = Math.Max(1, (hits.Count + EngineApi.ResultChunkHits - 1) / EngineApi.ResultChunkHits);
            for (var part = 0; part < parts; part++)
            {
                var chunk = hits.Skip(part * EngineApi.ResultChunkHits).Take(EngineApi.ResultChunkHits).ToList();
                var last = part == parts - 1;
                if (!await client.PostResultAsync(runId, part, last, chunk, last ? stats : null, ct, last ? gone : null, last ? matches : null))
                    return Cancelled(runId);
            }

            logger.LogInformation("Прогон {Run}: коллизий {Hits}, загрузка {Load:0.0} с, поиск {Search:0.0} с", runId, hits.Count, stats.LoadSeconds,
                stats.BroadSeconds + stats.NarrowSeconds);
            return RunOutcome.Succeeded;
        }
        catch (ModelsNotAlignedException exception)
        {
            return await FailAsync(runId, NotAlignedMessage(exception, assignment.Models), retry: false, exception, ct);
        }
        catch (InvalidDataException exception)
        {
            return await FailAsync(runId, $"слепок модели повреждён: {exception.Message}", retry: false, exception, ct);
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException && !ct.IsCancellationRequested)
        {
            return await FailAsync(runId, $"не удалось получить слепки: {exception.Message}", retry: true, exception, ct);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return await FailAsync(runId, $"сбой движка: {exception.Message}", retry: false, exception, ct);
        }
    }

    /// <summary>
    ///     Оглавления и параметры выгрузок прошлого прогона. Выгрузки, которой уже нет на сервере, в списке не будет — тогда
    ///     прогон считается полностью, а пересозданные элементы этой модели не предлагаются.
    /// </summary>
    private async Task<IReadOnlyList<PreviousModel>> ReadPreviousAsync(Guid runId, IReadOnlyList<EngineRunModel> models, Dictionary<Guid, string> current,
        CancellationToken ct)
    {
        var result = new List<PreviousModel>(models.Count);
        foreach (var model in models)
        {
            string folder;
            try
            {
                folder = current.TryGetValue(model.SnapshotId, out var known) ? known : await cache.GetAsync(model, ct);
            }
            catch (HttpRequestException exception) when (exception.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                logger.LogWarning("Прогон {Run}: прошлой выгрузки {Model} уже нет — полный пересчёт", runId, model.ModelName);
                continue;
            }

            using var stream = File.OpenRead(Path.Combine(folder, SnapshotCache.Parameters));
            result.Add(new PreviousModel(model, ReadIndex(folder), ModelParametersFile.Read(stream), current.ContainsKey(model.SnapshotId)));
        }

        return result;
    }

    private static ModelIndex ReadIndex(string folder)
    {
        using var stream = File.OpenRead(Path.Combine(folder, SnapshotCache.Geometry));
        return ModelGeometryFile.ReadIndex(stream);
    }

    private static ModelInput Read(EngineRunModel model, string folder)
    {
        ModelGeometry geometry;
        using (var stream = File.OpenRead(Path.Combine(folder, SnapshotCache.Geometry))) geometry = ModelGeometryFile.Read(stream);
        ModelParameters parameters;
        using (var stream = File.OpenRead(Path.Combine(folder, SnapshotCache.Parameters))) parameters = ModelParametersFile.Read(stream);
        return new ModelInput(model.Discipline, geometry, parameters);
    }

    /// <summary>«Модели не совмещены с остальными: ОВ — до остальных 6 000 км. Проверьте общие координаты моделей».</summary>
    internal static string NotAlignedMessage(ModelsNotAlignedException exception, IReadOnlyList<EngineRunModel> models)
    {
        var names = models.ToDictionary(m => m.ModelId.ToString("N"), m => m.ModelName);
        var listed = exception.Models.Select(m => $"{names.GetValueOrDefault(m.ModelKey, m.ModelKey)} — до остальных {Distance(m.DistanceMm)}");
        return $"Модели не совмещены с остальными: {string.Join("; ", listed)}. Проверьте общие координаты моделей";

        static string Distance(double mm)
        {
            var culture = System.Globalization.CultureInfo.GetCultureInfo("ru-RU");
            return mm < 1_000_000 ? (mm / 1000).ToString("N0", culture) + " м" : (mm / 1_000_000).ToString("N0", culture) + " км";
        }
    }

    private async Task<RunOutcome> FailAsync(Guid runId, string error, bool retry, Exception exception, CancellationToken ct)
    {
        logger.LogError(exception, "Прогон {Run} не удался", runId);
        try
        {
            await client.FailAsync(runId, error, retry, ct);
        }
        catch (HttpRequestException report)
        {
            logger.LogWarning(report, "Сбой прогона {Run} не дошёл до сервера — его вернёт в очередь истёкшая аренда", runId);
        }

        return RunOutcome.Failed;
    }

    private RunOutcome Cancelled(Guid runId)
    {
        logger.LogInformation("Прогон {Run} отменён на сервере", runId);
        return RunOutcome.Cancelled;
    }

    private static async Task IgnoreCancellation(Task task)
    {
        try
        {
            await task;
        }
        catch (OperationCanceledException)
        {
        }
    }
}

/// <summary>Коллизии движка → строки результата: раздел, наборы, категория, семейство, тип, уровень и ElementId каждой стороны.</summary>
public static class HitEnricher
{
    public static List<EngineHit> Enrich(IReadOnlyList<ClashHit> hits, IReadOnlyList<ModelInput> models, ClashConfiguration config)
    {
        var membership = SetMembership.Evaluate(config, models);
        var elements = new Dictionary<(string Model, string UniqueId), (ElementInfo Info, string Discipline, ulong Fingerprint)>();
        foreach (var model in models)
        foreach (var element in model.Geometry.Elements)
            elements[(model.Geometry.Header.ModelKey, element.Info.UniqueId)] = (element.Info, model.Discipline, element.Fingerprint);

        var result = new List<EngineHit>(hits.Count);
        foreach (var hit in hits)
        {
            var (a, disciplineA, fingerprintA) = elements[(hit.A.ModelKey, hit.A.UniqueId)];
            var (b, disciplineB, fingerprintB) = elements[(hit.B.ModelKey, hit.B.UniqueId)];
            result.Add(new EngineHit(
                hit.A.ModelKey, a.UniqueId, a.ElementId, disciplineA, membership.SetsOf(hit.A.ModelKey, a.UniqueId), a.Category, a.Family, a.Type, a.Level,
                hit.B.ModelKey, b.UniqueId, b.ElementId, disciplineB, membership.SetsOf(hit.B.ModelKey, b.UniqueId), b.Category, b.Family, b.Type, b.Level,
                hit.PointX, hit.PointY, hit.PointZ, hit.DepthMm, hit.Checks.Select(c => c.Key).ToList(),
                unchecked((long)fingerprintA), unchecked((long)fingerprintB)));
        }

        return result;
    }
}
