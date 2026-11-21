using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using ToadFlow.Agent.Config;
using ToadFlow.Agent.Server;
using ToadFlow.Contracts.Families;

namespace ToadFlow.Agent.Families;

/// <summary>
///     Отправляет отчёт обхода библиотеки: <c>POST /api/agent/families/scans/{scanId}</c>, тело сжато gzip. Сбой сети и ответы 5xx
///     повторяются с нарастающей паузой, пока не выйдет <see cref="RetryBudget" />; потом отчёт отбрасывается — сервер попросит
///     новый обход. Отказ сервера (4xx) не повторяется.
/// </summary>
/// <param name="retryDelays">Паузы между повторами; дальше — последняя. null — 5, 15, 30, 60, 120 с.</param>
/// <param name="retryBudget">Сколько повторять после первого сбоя; null — <see cref="RetryBudget" />.</param>
public sealed class FamilyScanReporter(AgentConfig config, AgentOptions options, ILogger<FamilyScanReporter> logger, IReadOnlyList<TimeSpan>? retryDelays = null,
    TimeSpan? retryBudget = null)
{
    public static readonly TimeSpan RetryBudget = TimeSpan.FromMinutes(10);

    private static readonly TimeSpan[] DefaultDelays =
        [TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(60), TimeSpan.FromSeconds(120)];

    private readonly IReadOnlyList<TimeSpan> _delays = retryDelays is { Count: > 0 } ? retryDelays : DefaultDelays;
    private readonly TimeSpan _budget = retryBudget ?? RetryBudget;

    /// <returns>true — сервер принял отчёт; false — отказал или не ответил за отведённое время.</returns>
    public async Task<bool> SendAsync(FamilyScanReportDto report, CancellationToken ct)
    {
        var body = Gzip(report);
        using var http = new HttpClient(options.HttpMessageHandlerFactory?.Invoke() ?? new HttpClientHandler(), disposeHandler: true)
        {
            BaseAddress = new Uri(config.ServerUrl.TrimEnd('/') + "/"),
            Timeout = TimeSpan.FromMinutes(5)
        };
        http.DefaultRequestHeaders.Add("X-Agent-Id", config.AgentId.ToString());
        http.DefaultRequestHeaders.Add("X-Agent-Key", config.Key);

        Stopwatch? failing = null;
        for (var attempt = 0; ; attempt++)
        {
            string error;
            try
            {
                using var content = new ByteArrayContent(body);
                content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
                content.Headers.ContentEncoding.Add("gzip");
                using var response = await http.PostAsync($"api/agent/families/scans/{report.ScanId}", content, ct);
                if (response.IsSuccessStatusCode) return true;
                if ((int)response.StatusCode is >= 400 and < 500 && response.StatusCode is not (HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests))
                {
                    logger.LogWarning("Сервер не принял отчёт обхода библиотеки: {Status}", (int)response.StatusCode);
                    return false;
                }

                error = $"сервер ответил {(int)response.StatusCode}";
            }
            catch (Exception exception) when (exception is HttpRequestException or IOException or TaskCanceledException && !ct.IsCancellationRequested)
            {
                error = exception.Message;
            }

            if ((failing ??= Stopwatch.StartNew()).Elapsed >= _budget)
            {
                logger.LogWarning("Отчёт обхода библиотеки не отправлен ({Error}) — отбрасываю, сервер попросит новый обход", error);
                return false;
            }

            var delay = _delays[Math.Min(attempt, _delays.Count - 1)];
            logger.LogWarning("Отчёт обхода библиотеки не отправлен ({Error}) — повтор через {Delay} с", error, delay.TotalSeconds);
            await Task.Delay(delay, ct);
        }
    }

    private static byte[] Gzip(FamilyScanReportDto report)
    {
        using var packed = new MemoryStream();
        using (var zip = new GZipStream(packed, CompressionLevel.Fastest, leaveOpen: true)) JsonSerializer.Serialize(zip, report, JsonSerializerOptions.Web);
        return packed.ToArray();
    }
}
