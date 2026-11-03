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
///     и сдать частями. Ход идёт на сервер таймером весь прогон, в каждой фазе (скачивание прошлых слепков, поиск, сверка,
///     сжатие и сдача частей) — так сервер видит, что работник жив, а работник узнаёт об отмене (409) и бросает работу.
///     Каждый запрос несёт номер попытки: ответ другой попытки сервер отвергает.
/// </summary>
public sealed class EngineWorker(EngineClient client, SnapshotCache cache, WorkerOptions options, ILogger<EngineWorker> logger, TimeProvider? time = null)
{
    private readonly TimeProvider _time = time ?? TimeProvider.System;

    /// <summary>Текущая стадия — ту же строку таймер хода шлёт серверу.</summary>
    private sealed record Stage(string Name, string Text);

    /// <summary>Сервер сообщил, что прогон больше не идёт (отменён, отдан другой попытке).</summary>
    private sealed class Stopped
    {
        private int _value;
        public bool ByServer => Volatile.Read(ref _value) == 1;
        public void Set() => Volatile.Write(ref _value, 1);
    }

    /// <returns>Итог прогона; null — очередь пуста.</returns>
    public async Task<RunOutcome?> RunOnceAsync(CancellationToken ct)
    {
        var assignment = await client.ClaimAsync(ct);
        return assignment is null ? null : await ProcessAsync(assignment, ct);
    }

    public async Task<RunOutcome> ProcessAsync(EngineRunAssignment assignment, CancellationToken ct)
    {
        var runId = assignment.RunId;
        var attempt = assignment.Attempt;
        using var cancelled = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var token = cancelled.Token;
        var stopped = new Stopped();
        var stage = new Stage("Загрузка моделей", "");
        var phase = "получить слепки";
        logger.LogInformation("Прогон {Run}: моделей {Models}, попытка {Attempt}", runId, assignment.Models.Count, attempt);

        Task<bool> Report(string name, string text)
        {
            Volatile.Write(ref stage, new Stage(name, text));
            return client.ProgressAsync(runId, attempt, name, text, token);
        }

        // Таймер хода запускается до первого запроса и работает до конца прогона, что бы ни делал основной поток.
        using var timer = new PeriodicTimer(options.ProgressInterval, _time);
        var heartbeat = BeatAsync(timer, runId, attempt, () => Volatile.Read(ref stage), stopped, cancelled);
        try
        {
            var load = Stopwatch.StartNew();
            var models = new List<ModelInput>(assignment.Models.Count);
            var folders = new Dictionary<Guid, string>();
            for (var i = 0; i < assignment.Models.Count; i++)
            {
                var model = assignment.Models[i];
                if (!await Report("Загрузка моделей", $"{i + 1} из {assignment.Models.Count}: {model.ModelName}")) return Cancelled(runId);
                folders[model.SnapshotId] = await cache.GetAsync(model, token);
                models.Add(Read(model, folders[model.SnapshotId]));
            }

            var config = ConfigurationJson.Deserialize(assignment.ConfigurationJson);
            EngineBasis? basis = null;
            IReadOnlyList<PreviousModel> previous = [];
            ClashConfiguration? previousConfig = null;
            IncrementalBase? incremental = null;
            if (assignment.Kind == EngineApi.Approved)
            {
                Volatile.Write(ref stage, new Stage("Загрузка прошлых слепков", ""));
                basis = await client.BasisAsync(runId, attempt, token);
                if (basis is null) return Cancelled(runId);
                if (basis.PreviousConfigurationJson is { } previousJson)
                {
                    previous = await ReadPreviousAsync(runId, basis.PreviousModels, folders, token);
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

            if (!await Report("Поиск пересечений", "")) return Cancelled(runId);
            var result = await Task.Run(() => new ClashEngine(new EngineOptions(options.Threads)).Run(models, config, token, incremental), token);
            if (!await Report("Запись результата", $"коллизий {result.Hits.Count}")) return Cancelled(runId);
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
                Volatile.Write(ref stage, new Stage("Сверка с прошлым прогоном", $"открытых пар {basis.Open.Count}"));
                gone = ApprovedRunFacts.Gone(basis.Open, result.Hits, models, previousConfig, options.Threads, token);
                var current = assignment.Models.ToDictionary(m => m.ModelId.ToString("N"), m => ReadIndex(folders[m.SnapshotId]));
                matches = ApprovedRunFacts.Matches(previous, current, result.Hits, basis.Open,
                    model => logger.LogWarning("Прогон {Run}: модель {Model} слишком плотная для сопоставления пересозданных элементов — предложений нет",
                        runId, model));
            }

            phase = "сдать результат";
            var parts = Math.Max(1, (hits.Count + EngineApi.ResultChunkHits - 1) / EngineApi.ResultChunkHits);
            for (var part = 0; part < parts; part++)
            {
                var chunk = hits.Skip(part * EngineApi.ResultChunkHits).Take(EngineApi.ResultChunkHits).ToList();
                var last = part == parts - 1;
                Volatile.Write(ref stage, new Stage("Запись результата", $"часть {part + 1} из {parts}"));
                if (!await client.PostResultAsync(runId, attempt, part, last, chunk, last ? stats : null, token, last ? gone : null, last ? matches : null))
                    return Cancelled(runId);
            }

            logger.LogInformation("Прогон {Run}: коллизий {Hits}, загрузка {Load:0.0} с, поиск {Search:0.0} с", runId, hits.Count, stats.LoadSeconds,
                stats.BroadSeconds + stats.NarrowSeconds);
            return RunOutcome.Succeeded;
        }
        catch (OperationCanceledException) when (stopped.ByServer && !ct.IsCancellationRequested)
        {
            return Cancelled(runId);
        }
        catch (ModelsNotAlignedException exception)
        {
            return await FailAsync(runId, attempt, NotAlignedMessage(exception, assignment.Models), retry: false, exception, ct);
        }
        catch (InvalidDataException exception)
        {
            return await FailAsync(runId, attempt, $"слепок модели повреждён: {exception.Message}", retry: false, exception, ct);
        }
        catch (EngineServerException exception)
        {
            // Сервер ответил по существу (ошибка сверки, базы, отказ): его текст — причина прогона. Пересчитывать ради детерминированной
            // ошибки сервера незачем; повторяется только то, что может пройти (шлюз, перегрузка).
            return await FailAsync(runId, attempt, $"сервер не принял результат ({exception.StatusCode}): {exception.Body}", exception.Transient, exception, ct);
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException && !ct.IsCancellationRequested)
        {
            return await FailAsync(runId, attempt, $"не удалось {phase}: {exception.Message}", retry: true, exception, ct);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return await FailAsync(runId, attempt, $"сбой движка: {exception.Message}", retry: false, exception, ct);
        }
        finally
        {
            await cancelled.CancelAsync();
            await heartbeat;
        }
    }

    /// <summary>
    ///     Ход по таймеру: серверу уходит текущая стадия, пока идёт прогон. Сбой связи ход не обрывает — следующий тик повторит; если
    ///     сервер ответил, что прогон не идёт, работа прекращается.
    /// </summary>
    private async Task BeatAsync(PeriodicTimer timer, Guid runId, int attempt, Func<Stage> current, Stopped stopped, CancellationTokenSource cancelled)
    {
        try
        {
            while (await timer.WaitForNextTickAsync(cancelled.Token))
            {
                var stage = current();
                try
                {
                    if (await client.ProgressAsync(runId, attempt, stage.Name, stage.Text, cancelled.Token)) continue;
                    stopped.Set();
                    await cancelled.CancelAsync();
                    return;
                }
                catch (Exception exception) when (exception is HttpRequestException or IOException)
                {
                    logger.LogWarning("Прогон {Run}: ход не дошёл до сервера ({Error}) — повтор на следующем тике", runId, exception.Message);
                }
            }
        }
        catch (OperationCanceledException)
        {
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

    private async Task<RunOutcome> FailAsync(Guid runId, int attempt, string error, bool retry, Exception exception, CancellationToken ct)
    {
        logger.LogError(exception, "Прогон {Run} не удался", runId);
        try
        {
            await client.FailAsync(runId, attempt, error, retry, ct);
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
