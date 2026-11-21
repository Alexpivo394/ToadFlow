using System.Security.Cryptography;
using System.Text;
using Alexpivo394.Revit.Export.Sources;
using Alexpivo394.Revit.Families.Files.Scan;
using Microsoft.Extensions.Logging;
using ToadFlow.Contracts.Families;

namespace ToadFlow.Agent.Families;

/// <summary>
///     Обход библиотеки семейств без Revit по просьбе сервера: файлы с размером, временем, хешем и версией Revit, копии нового
///     содержимого в <c>.toadflow\history</c>, отчёт серверу. Одновременно идёт один обход; кеш хешей — в папке данных агента,
///     свой файл на каждый корень библиотеки.
/// </summary>
public sealed class FamilyLibraryScan : IDisposable
{
    private readonly Func<FamilyScanReportDto, CancellationToken, Task> _send;
    private readonly string _cacheFolder;
    private readonly ILogger<FamilyLibraryScan> _logger;
    private readonly CancellationTokenSource _stopping = new();
    private readonly LibraryWalker _walker = new(RvtFileInfo.TryReadYear);
    private readonly Lock _gate = new();

    public FamilyLibraryScan(FamilyScanReporter reporter, ILogger<FamilyLibraryScan> logger)
        : this(async (report, ct) => await reporter.SendAsync(report, ct), Path.Combine(AgentPaths.Root, "families"), logger)
    {
    }

    /// <param name="send">Отправить отчёт серверу.</param>
    /// <param name="cacheFolder">Где держать кеш хешей.</param>
    internal FamilyLibraryScan(Func<FamilyScanReportDto, CancellationToken, Task> send, string cacheFolder, ILogger<FamilyLibraryScan> logger)
    {
        _send = send;
        _cacheFolder = cacheFolder;
        _logger = logger;
    }

    /// <summary>Идущий или последний обход вместе с отправкой отчёта; null — обходов не было.</summary>
    internal Task? Current { get; private set; }

    /// <summary>Начать обход в фоне; false — прошлый ещё идёт (обход и отправка отчёта).</summary>
    public bool Start(FamilyScanRequestDto request)
    {
        lock (_gate)
        {
            if (Current is { IsCompleted: false }) return false;
            Current = Task.Run(() => RunAsync(request, _stopping.Token));
            return true;
        }
    }

    public void Dispose()
    {
        _stopping.Cancel();
        _stopping.Dispose();
    }

    private async Task RunAsync(FamilyScanRequestDto request, CancellationToken ct)
    {
        try
        {
            var report = Walk(request, ct);
            _logger.LogInformation("Обход библиотеки {Root}: файлов {Files}, не взято {Unsettled}, ошибок {Errors}{Incomplete}", request.Root,
                report.Files.Count, report.Unsettled.Count, report.Errors.Count, report.Complete ? "" : " — неполный");
            await _send(report, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // агент останавливается
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Обход библиотеки {Root} не удался", request.Root);
        }
    }

    private FamilyScanReportDto Walk(FamilyScanRequestDto request, CancellationToken ct)
    {
        if (!Directory.Exists(request.Root))
            return new FamilyScanReportDto(request.ScanId, false, [], [$"{request.Root}: папка библиотеки недоступна"], []);

        var cachePath = CachePath(request.Root);
        var cache = ScanCache.Load(cachePath);
        var walk = _walker.Walk(request.Root, cache, ct);

        // Новое содержимое сразу копируется в историю. Не скопировалось (файл изменился после обхода, не читается, в .toadflow
        // нельзя писать) — файл не отдаётся с этим хешем: у версии не было бы копии. Карточка остаётся как была, возьмёт следующий обход.
        var files = new List<FamilyScanFileDto>(walk.Files.Count);
        var unsettled = walk.Unsettled.ToList();
        var errors = walk.Errors.ToList();
        foreach (var file in walk.Files)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                HistoryStore.Keep(request.Root, file);
                files.Add(new FamilyScanFileDto(file.RelativePath, file.Size, file.ModifiedUtc, file.Hash, file.CatalogHash, file.RevitYear));
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                unsettled.Add(file.RelativePath);
                errors.Add($"{file.RelativePath}: копия версии не сохранена — {exception.Message}");
            }
        }

        try
        {
            cache.Save(cachePath);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(exception, "Кеш обхода библиотеки не сохранён — следующий обход пересчитает хеши");
        }

        return new FamilyScanReportDto(request.ScanId, walk.Complete, files, errors, unsettled);
    }

    /// <summary>Свой кеш на каждый корень: агент могли переназначить на другую библиотеку.</summary>
    private string CachePath(string root)
    {
        var key = root.TrimEnd('\\', '/').ToLowerInvariant();
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)))[..16].ToLowerInvariant();
        return Path.Combine(_cacheFolder, $"scan-{hash}.cache");
    }
}
