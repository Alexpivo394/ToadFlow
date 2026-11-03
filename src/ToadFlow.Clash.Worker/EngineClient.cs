using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using ToadFlow.Contracts.Clash;

namespace ToadFlow.Clash.Worker;

/// <summary>Настройки работника — секция <c>Clash</c> конфигурации (в Docker: <c>Clash__ServerUrl</c>, <c>Clash__EngineKey</c> …).</summary>
public sealed class WorkerOptions
{
    /// <summary>Адрес сервера ToadFlow; работнику нужен только он.</summary>
    public string ServerUrl { get; set; } = "http://localhost:5000";

    /// <summary>Ключ работника — тот же, что <c>Clash:EngineKey</c> сервера.</summary>
    public string EngineKey { get; set; } = "";

    /// <summary>Кеш слепков; пусто — <c>%LocalAppData%\ToadFlow\clash-engine-cache</c>.</summary>
    public string CacheRoot { get; set; } = "";

    public long CacheLimitBytes { get; set; } = 30L * 1024 * 1024 * 1024;

    /// <summary>Как часто спрашивать прогон, когда очередь пуста.</summary>
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>Как часто сообщать ход во время поиска: сервер держит аренду 2 мин.</summary>
    public TimeSpan ProgressInterval { get; set; } = TimeSpan.FromSeconds(20);

    /// <summary>Потоков поиска; 0 — все ядра (в контейнере — лимит cpus).</summary>
    public int Threads { get; set; }

    public string ResolvedCacheRoot => CacheRoot.Length > 0
        ? CacheRoot
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ToadFlow", "clash-engine-cache");
}

/// <summary>
///     Протокол работника с сервером (<c>/api/engine</c>). Каждый запрос о прогоне несёт номер попытки. Ответ 409 — прогон больше не идёт:
///     его отменили или отдали другой попытке.
/// </summary>
public sealed class EngineClient(HttpClient http)
{
    private static readonly JsonSerializerOptions Json = JsonSerializerOptions.Web;
    private int _downloads;

    /// <summary>Сколько файлов слепков скачано (для тестов кеша).</summary>
    public int Downloads => Volatile.Read(ref _downloads);

    public async Task<EngineRunAssignment?> ClaimAsync(CancellationToken ct)
    {
        using var response = await http.PostAsync("api/engine/runs/claim", null, ct);
        if (response.StatusCode == HttpStatusCode.NoContent) return null;
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<EngineRunAssignment>(Json, ct);
    }

    /// <returns>false — прогон больше не идёт.</returns>
    public async Task<bool> ProgressAsync(Guid runId, int attempt, string stage, string text, CancellationToken ct)
    {
        using var response = await http.PostAsJsonAsync($"api/engine/runs/{runId}/progress", new EngineProgress(runId, stage, text, attempt), Json, ct);
        if (response.StatusCode == HttpStatusCode.Conflict) return false;
        response.EnsureSuccessStatusCode();
        return true;
    }

    /// <summary>Скачивает файл слепка в <paramref name="path" />; начатый файл докачивается с места обрыва.</summary>
    public async Task DownloadAsync(Guid snapshotId, string file, string path, CancellationToken ct)
    {
        Interlocked.Increment(ref _downloads);
        var offset = File.Exists(path) ? new FileInfo(path).Length : 0;
        using var request = new HttpRequestMessage(HttpMethod.Get, $"api/engine/snapshots/{snapshotId}/{file}");
        if (offset > 0) request.Headers.Range = new RangeHeaderValue(offset, null);
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        if (response.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable) return; // уже целиком
        response.EnsureSuccessStatusCode();

        var append = response.StatusCode == HttpStatusCode.PartialContent;
        await using var target = new FileStream(path, append ? FileMode.Append : FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20, useAsync: true);
        await using var source = await response.Content.ReadAsStreamAsync(ct);
        await source.CopyToAsync(target, ct);
    }

    /// <summary>Основа утверждённого прогона: прошлый прогон и открытые коллизии.</summary>
    /// <returns>null — прогон больше не идёт.</returns>
    public async Task<EngineBasis?> BasisAsync(Guid runId, int attempt, CancellationToken ct)
    {
        using var response = await http.GetAsync($"api/engine/runs/{runId}/basis?{EngineApi.AttemptQuery}={attempt}", ct);
        if (response.StatusCode == HttpStatusCode.Conflict) return null;
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<EngineBasis>(Json, ct);
    }

    /// <param name="gone">Только в последней части утверждённого прогона: открытые пары, которых нет среди найденных.</param>
    /// <param name="matches">Только в последней части утверждённого прогона: предложения сопоставить пересозданные элементы.</param>
    /// <returns>false — прогон больше не идёт.</returns>
    /// <exception cref="EngineServerException">Сервер отклонил или не смог принять результат (не 409): причина — в ответе сервера.</exception>
    public async Task<bool> PostResultAsync(Guid runId, int attempt, int part, bool last, IReadOnlyList<EngineHit> hits, EngineRunStats? stats, CancellationToken ct,
        IReadOnlyList<EngineGonePair>? gone = null, IReadOnlyList<EngineIdentityMatch>? matches = null)
    {
        using var packed = new MemoryStream();
        await using (var zip = new GZipStream(packed, CompressionLevel.Fastest, leaveOpen: true))
            await JsonSerializer.SerializeAsync(zip, new EngineRunResult(runId, hits, stats, gone, matches, attempt), Json, ct);
        using var content = new ByteArrayContent(packed.ToArray());
        content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        content.Headers.ContentEncoding.Add("gzip");
        using var response = await http.PostAsync($"api/engine/runs/{runId}/result?part={part}&last={(last ? "true" : "false")}", content, ct);
        if (response.StatusCode == HttpStatusCode.Conflict) return false;
        if (!response.IsSuccessStatusCode) throw await EngineServerException.ReadAsync(response, ct);
        return true;
    }

    public async Task FailAsync(Guid runId, int attempt, string error, bool retry, CancellationToken ct)
    {
        using var response = await http.PostAsJsonAsync($"api/engine/runs/{runId}/fail", new EngineFailure(runId, error, retry, attempt), Json, ct);
        if (response.StatusCode != HttpStatusCode.Conflict) response.EnsureSuccessStatusCode();
    }
}

/// <summary>Сервер ответил на сдачу результата ошибкой (не 409): сбой сверки, ошибка базы, отказ по содержимому. Это не сбой сети и не повод качать слепки заново.</summary>
public sealed class EngineServerException(int statusCode, string body) : Exception($"сервер ответил {statusCode}: {body}")
{
    private const int MaxBody = 500;

    public int StatusCode { get; } = statusCode;

    /// <summary>Текст ответа сервера, до 500 знаков.</summary>
    public string Body { get; } = body;

    /// <summary>
    ///     Сбой может не повториться: ошибка сервера (5xx — база, перезапуск, шлюз) или перегрузка (408, 429); расход ограничен числом
    ///     попыток прогона. Остальное 4xx — решение сервера по этому результату, повтор бесполезен.
    /// </summary>
    public bool Transient => StatusCode is >= 500 or 408 or 429;

    internal static async Task<EngineServerException> ReadAsync(HttpResponseMessage response, CancellationToken ct)
    {
        var text = (await response.Content.ReadAsStringAsync(ct)).Trim();
        if (text.Length > MaxBody) text = text[..MaxBody] + "…";
        return new EngineServerException((int)response.StatusCode, text.Length == 0 ? response.ReasonPhrase ?? "" : text);
    }
}
