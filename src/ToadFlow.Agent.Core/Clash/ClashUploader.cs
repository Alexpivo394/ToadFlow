using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using ToadFlow.Agent.Config;
using ToadFlow.Agent.Jobs;
using ToadFlow.Agent.Server;
using ToadFlow.Contracts;
using ToadFlow.Contracts.Clash;

namespace ToadFlow.Agent.Clash;

/// <summary>
///     Загружает на сервер слепок, который аддин оставил в <see cref="ClashStaging" />, и подменяет итог задания на
///     <see cref="ClashExtractResult" />. Части по <see cref="ClashUploads.MaxChunkBytes" />, сжатые gzip; после обрыва
///     загрузка продолжается с того места, которое назовёт сервер. Повторы идут, пока не выйдет <see cref="RetryBudget" />: этого
///     хватает на перезапуск сервера. Загруженный слепок удаляется; не загруженный из-за связи — откладывается, и следующая попытка
///     того же задания на этом агенте загрузит его без нового запуска Revit (<see cref="TakeKept" />).
/// </summary>
/// <param name="retryDelays">Паузы между повторами; дальше — последняя. null — 2, 5, 10, 30, 60 с.</param>
/// <param name="retryBudget">Сколько повторять одну фазу загрузки после первого сбоя; null — <see cref="RetryBudget" />.</param>
public sealed class ClashUploader(AgentConfig config, AgentOptions options, ILogger<ClashUploader> logger, IReadOnlyList<TimeSpan>? retryDelays = null,
    TimeSpan? retryBudget = null)
{
    public static readonly TimeSpan RetryBudget = TimeSpan.FromMinutes(30);

    private const string Stage = "Загрузка слепка";
    private const string StagedFile = "staged.json";
    private static readonly TimeSpan[] DefaultDelays = [TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(60)];
    private static readonly TimeSpan ProgressEvery = TimeSpan.FromSeconds(5);
    private static readonly JsonSerializerOptions Json = JsonSerializerOptions.Web;

    private readonly IReadOnlyList<TimeSpan> _delays = retryDelays is { Count: > 0 } ? retryDelays : DefaultDelays;
    private readonly TimeSpan _budget = retryBudget ?? RetryBudget;

    /// <summary>
    ///     Слепок, отложенный прошлой попыткой задания (загрузка не удалась из-за связи), — переносится в папку этой попытки. Возвращает
    ///     итог «выгружено», как его вернул бы аддин; null — отложенного слепка нет, нужен Revit.
    /// </summary>
    public JobCompletionDto? TakeKept(JobAssignmentDto assignment)
    {
        if (assignment.ModuleKind != ModuleKinds.ClashExtract) return null;
        var kept = ClashStaging.Kept(assignment.JobId);
        try
        {
            if (!File.Exists(Path.Combine(kept, StagedFile))) return null;
            var staged = JsonSerializer.Deserialize<ClashStagedSnapshot>(File.ReadAllText(Path.Combine(kept, StagedFile)), Json);
            if (staged is null) return null;
            var folder = ClashStaging.Folder(assignment.JobId, assignment.Attempt);
            Delete(folder);
            Directory.Move(kept, folder);
            logger.LogInformation("Задание {Job}, попытка {Attempt}: загружаю слепок, отложенный прошлой попыткой, — Revit не нужен", assignment.JobId,
                assignment.Attempt);
            return new JobCompletionDto(assignment.JobId, JobOutcome.Succeeded, "слепок выгружен прошлой попыткой", JsonSerializer.Serialize(staged with { Folder = folder }, Json),
                assignment.Attempt);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            logger.LogWarning(exception, "Отложенный слепок задания {Job} не прочитан — выгружаю заново", assignment.JobId);
            return null;
        }
    }

    /// <summary>Итог задания после загрузки; для заданий другого вида и неуспешных выгрузок — без изменений.</summary>
    /// <param name="slot">Слот экземпляра, в журнал которого идут строки загрузки.</param>
    public async Task<JobCompletionDto> FinishAsync(JobAssignmentDto assignment, int slot, JobCompletionDto completion, IJobReporter reporter, CancellationToken ct)
    {
        if (assignment.ModuleKind != ModuleKinds.ClashExtract) return completion;

        var folder = ClashStaging.Folder(assignment.JobId, assignment.Attempt);
        try
        {
            if (completion.Outcome is not (JobOutcome.Succeeded or JobOutcome.Warning)) return completion;

            var staged = Parse(completion.ResultJson);
            if (staged is null) return completion with { Outcome = JobOutcome.Failed, Note = "аддин не сообщил, где слепок", ResultJson = null };
            if (!ClashStaging.IsInside(staged.Folder))
            {
                folder = null;
                return completion with { Outcome = JobOutcome.Failed, Note = "аддин вернул папку слепка вне рабочей папки", ResultJson = null };
            }

            folder = staged.Folder;
            var log = new UploadLog(assignment.JobId, assignment.Attempt, slot, reporter, logger);
            try
            {
                var result = await UploadAsync(assignment.JobId, assignment.Attempt, folder, log, ct);
                return completion with { ResultJson = JsonSerializer.Serialize(result, Json) };
            }
            catch (UploadRejectedException rejected)
            {
                await log.WriteAsync($"сервер не принял слепок: {rejected.Message}");
                return completion with { Outcome = JobOutcome.Failed, Note = $"сервер не принял слепок: {rejected.Message}", ResultJson = null };
            }
            catch (Exception exception) when (exception is HttpRequestException or IOException or TaskCanceledException or UploadRetryException
                                                  && !ct.IsCancellationRequested)
            {
                logger.LogWarning(exception, "Слепок задания {Job} не загружен", assignment.JobId);
                var kept = Keep(assignment.JobId, staged);
                await log.WriteAsync($"слепок не загружен: {exception.Message}{(kept ? " — сохранён, повтор загрузит его без Revit" : "")}");
                if (kept) folder = null;
                return completion with { Outcome = JobOutcome.InfrastructureFailure, Note = $"не удалось загрузить выгрузку: {exception.Message}", ResultJson = null };
            }
        }
        finally
        {
            Delete(folder);
        }
    }

    /// <summary>Папки слепков старше суток — от заданий, которые агент не успел загрузить (упал, остановлен).</summary>
    public static void CleanupStale(TimeSpan olderThan)
    {
        if (!Directory.Exists(ClashStaging.Root)) return;
        foreach (var folder in Directory.EnumerateDirectories(ClashStaging.Root))
        {
            if (DateTime.UtcNow - Directory.GetLastWriteTimeUtc(folder) > olderThan) Delete(folder);
        }
    }

    private async Task<ClashExtractResult> UploadAsync(Guid jobId, int attempt, string folder, UploadLog log, CancellationToken ct)
    {
        var geometry = Path.Combine(folder, ClashUploads.FileName(ClashUploads.Geometry)!);
        var parameters = Path.Combine(folder, ClashUploads.FileName(ClashUploads.Parameters)!);
        if (!File.Exists(geometry) || !File.Exists(parameters)) throw new UploadRejectedException("в папке слепка нет файлов model.tcg и model.tcp");

        var request = new ClashUploadRequest(jobId, new FileInfo(geometry).Length, await ShaAsync(geometry, ct), new FileInfo(parameters).Length,
            await ShaAsync(parameters, ct), attempt);
        var stopwatch = Stopwatch.StartNew();
        using var http = new HttpClient(options.HttpMessageHandlerFactory?.Invoke() ?? new HttpClientHandler(), disposeHandler: true)
        {
            BaseAddress = new Uri(config.ServerUrl.TrimEnd('/') + "/"),
            Timeout = TimeSpan.FromMinutes(5)
        };
        http.DefaultRequestHeaders.Add("X-Agent-Id", config.AgentId.ToString());
        http.DefaultRequestHeaders.Add("X-Agent-Key", config.Key);

        var state = await RetryAsync(() => StartAsync(http, request, ct), ct);
        var uploadId = state.UploadId;
        state = await RetryAsync(() => SendFileAsync(http, uploadId, ClashUploads.Geometry, geometry, "геометрии", log, ct), ct);
        state = await RetryAsync(() => SendFileAsync(http, uploadId, ClashUploads.Parameters, parameters, "параметров", log, ct), ct);
        var result = await RetryAsync(() => CompleteAsync(http, uploadId, ct), ct);

        var megabytes = (request.GeometryBytes + request.ParametersBytes) / (1024.0 * 1024.0);
        await log.WriteAsync($"слепок загружен: {megabytes:0.#} МБ за {stopwatch.Elapsed.TotalSeconds:0.#} с, элементов {result.Elements}");
        return result;
    }

    private static async Task<ClashUploadState> StartAsync(HttpClient http, ClashUploadRequest request, CancellationToken ct)
    {
        using var response = await http.PostAsJsonAsync("api/agent/clash/uploads", request, Json, ct);
        await ThrowIfFailedAsync(response, ct);
        return (await response.Content.ReadFromJsonAsync<ClashUploadState>(Json, ct))!;
    }

    /// <summary>Досылает файл с того места, до которого его принял сервер. Повтор после обрыва начинается с вопроса серверу.</summary>
    private static async Task<ClashUploadState> SendFileAsync(HttpClient http, Guid uploadId, string file, string path, string what, UploadLog log,
        CancellationToken ct)
    {
        var state = (await http.GetFromJsonAsync<ClashUploadState>($"api/agent/clash/uploads/{uploadId}", Json, ct))!;
        var total = new FileInfo(path).Length;
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, useAsync: true);
        var buffer = new byte[ClashUploads.MaxChunkBytes];
        var received = Received(state, file);
        while (received < total)
        {
            stream.Seek(received, SeekOrigin.Begin);
            var read = await stream.ReadAtLeastAsync(buffer, (int)Math.Min(buffer.Length, total - received), throwOnEndOfStream: true, ct);
            using var content = Gzip(buffer.AsSpan(0, read));
            using var response = await http.PutAsync($"api/agent/clash/uploads/{uploadId}/{file}?offset={received}", content, ct);
            if (response.StatusCode != HttpStatusCode.Conflict) await ThrowIfFailedAsync(response, ct);
            var next = await response.Content.ReadFromJsonAsync<ClashUploadState>(Json, ct);
            // 409 с состоянием загрузки — сервер уже принял больше, продолжаем с его места; без состояния — задание больше не наше.
            if (next is null || next.UploadId != uploadId) throw new UploadRejectedException(await ErrorOf(response, ct));
            state = next;
            received = Received(state, file);
            await log.ProgressAsync($"загрузка {what}: {received / (1024.0 * 1024.0):0.#} из {total / (1024.0 * 1024.0):0.#} МБ");
        }

        return state;
    }

    private static async Task<ClashExtractResult> CompleteAsync(HttpClient http, Guid uploadId, CancellationToken ct)
    {
        using var response = await http.PostAsync($"api/agent/clash/uploads/{uploadId}/complete", null, ct);
        await ThrowIfFailedAsync(response, ct);
        return (await response.Content.ReadFromJsonAsync<ClashExtractResult>(Json, ct))!;
    }

    /// <summary>
    ///     Повторяет фазу загрузки, пока после первого сбоя не выйдет бюджет. Тайм-аут запроса <see cref="HttpClient" /> — это
    ///     <see cref="TaskCanceledException" /> не от токена агента: он тоже повторяется.
    /// </summary>
    private async Task<T> RetryAsync<T>(Func<Task<T>> call, CancellationToken ct)
    {
        Stopwatch? failing = null;
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                return await call();
            }
            catch (Exception exception) when (exception is HttpRequestException or IOException or UploadRetryException or TaskCanceledException
                                              && !ct.IsCancellationRequested && (failing ??= Stopwatch.StartNew()).Elapsed < _budget)
            {
                var delay = _delays[Math.Min(attempt, _delays.Count - 1)];
                logger.LogWarning("Загрузка слепка: {Error} — повтор через {Delay} с", exception.Message, delay.TotalSeconds);
                await Task.Delay(delay, ct);
            }
        }
    }

    /// <summary>Откладывает слепок для следующей попытки того же задания; false — отложить не вышло, папка будет удалена.</summary>
    private bool Keep(Guid jobId, ClashStagedSnapshot staged)
    {
        try
        {
            var kept = ClashStaging.Kept(jobId);
            Delete(kept);
            Directory.Move(staged.Folder, kept);
            File.WriteAllText(Path.Combine(kept, StagedFile), JsonSerializer.Serialize(staged with { Folder = kept }, Json));
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(exception, "Слепок задания {Job} не удалось отложить для повтора", jobId);
            return false;
        }
    }

    /// <summary>422 и 400 — сервер отверг слепок, повтор не поможет; 409 — задание больше не наше; 5xx и прочее — повторить.</summary>
    private static async Task ThrowIfFailedAsync(HttpResponseMessage response, CancellationToken ct)
    {
        if (response.IsSuccessStatusCode) return;
        var error = await ErrorOf(response, ct);
        throw response.StatusCode switch
        {
            HttpStatusCode.UnprocessableEntity or HttpStatusCode.BadRequest or HttpStatusCode.RequestEntityTooLarge => new UploadRejectedException(error),
            HttpStatusCode.Conflict => new UploadRejectedException(error),
            _ => new UploadRetryException($"сервер ответил {(int)response.StatusCode}: {error}")
        };
    }

    private static async Task<string> ErrorOf(HttpResponseMessage response, CancellationToken ct)
    {
        var text = await response.Content.ReadAsStringAsync(ct);
        try
        {
            using var document = JsonDocument.Parse(text);
            if (document.RootElement.TryGetProperty("error", out var error) && error.GetString() is { Length: > 0 } message) return message;
        }
        catch (JsonException)
        {
        }

        return text.Length > 0 ? text : response.ReasonPhrase ?? response.StatusCode.ToString();
    }

    private static ByteArrayContent Gzip(ReadOnlySpan<byte> chunk)
    {
        using var packed = new MemoryStream();
        using (var zip = new GZipStream(packed, CompressionLevel.Fastest, leaveOpen: true)) zip.Write(chunk);
        var content = new ByteArrayContent(packed.ToArray());
        content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        content.Headers.ContentEncoding.Add("gzip");
        return content;
    }

    private static long Received(ClashUploadState state, string file) => file == ClashUploads.Geometry ? state.GeometryReceived : state.ParametersReceived;

    private static async Task<string> ShaAsync(string path, CancellationToken ct)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, useAsync: true);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream, ct)).ToLowerInvariant();
    }

    private static ClashStagedSnapshot? Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            return JsonSerializer.Deserialize<ClashStagedSnapshot>(json, Json);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static void Delete(string? folder)
    {
        if (folder is null || !ClashStaging.IsInside(folder) || !Directory.Exists(folder)) return;
        try
        {
            Directory.Delete(folder, recursive: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // занята — уберёт CleanupStale при следующем запуске
        }
    }

    /// <summary>
    ///     Строки загрузки в журнал задания; ход — не чаще раза в 5 с. Журнал не обязателен: обрыв хаба или ошибка сервера при записи
    ///     строки не должны обрывать загрузку и итог задания.
    /// </summary>
    private sealed class UploadLog(Guid jobId, int attempt, int slot, IJobReporter reporter, ILogger logger)
    {
        private DateTimeOffset _lastProgress = DateTimeOffset.MinValue;

        public async Task WriteAsync(string text)
        {
            try
            {
                await reporter.ProgressAsync(new JobProgressDto(jobId, slot, [new JobLogDto(DateTimeOffset.UtcNow, Stage, text)], attempt));
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                logger.LogDebug(exception, "Строка журнала задания {Job} не дошла: {Text}", jobId, text);
            }
        }

        public Task ProgressAsync(string text)
        {
            var now = DateTimeOffset.UtcNow;
            if (now - _lastProgress < ProgressEvery) return Task.CompletedTask;
            _lastProgress = now;
            return WriteAsync(text);
        }
    }

    private sealed class UploadRejectedException(string message) : Exception(message);

    private sealed class UploadRetryException(string message) : Exception(message);
}
