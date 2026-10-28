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
///     загрузка продолжается с того места, которое назовёт сервер. Папка слепка удаляется в любом случае.
/// </summary>
public sealed class ClashUploader(AgentConfig config, AgentOptions options, ILogger<ClashUploader> logger, IReadOnlyList<TimeSpan>? retryDelays = null)
{
    private const string Stage = "Загрузка слепка";
    private static readonly TimeSpan[] DefaultDelays = [TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30)];
    private static readonly TimeSpan ProgressEvery = TimeSpan.FromSeconds(5);
    private static readonly JsonSerializerOptions Json = JsonSerializerOptions.Web;

    private readonly IReadOnlyList<TimeSpan> _delays = retryDelays ?? DefaultDelays;

    /// <summary>Итог задания после загрузки; для заданий другого вида и неуспешных выгрузок — без изменений.</summary>
    /// <param name="slot">Слот экземпляра, в журнал которого идут строки загрузки.</param>
    public async Task<JobCompletionDto> FinishAsync(JobAssignmentDto assignment, int slot, JobCompletionDto completion, IJobReporter reporter, CancellationToken ct)
    {
        if (assignment.ModuleKind != ModuleKinds.ClashExtract) return completion;

        var folder = ClashStaging.Folder(assignment.JobId);
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
            var log = new UploadLog(assignment.JobId, slot, reporter);
            try
            {
                var result = await UploadAsync(assignment.JobId, folder, log, ct);
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
                await log.WriteAsync($"слепок не загружен: {exception.Message}");
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

    private async Task<ClashExtractResult> UploadAsync(Guid jobId, string folder, UploadLog log, CancellationToken ct)
    {
        var geometry = Path.Combine(folder, ClashUploads.FileName(ClashUploads.Geometry)!);
        var parameters = Path.Combine(folder, ClashUploads.FileName(ClashUploads.Parameters)!);
        if (!File.Exists(geometry) || !File.Exists(parameters)) throw new UploadRejectedException("в папке слепка нет файлов model.tcg и model.tcp");

        var request = new ClashUploadRequest(jobId, new FileInfo(geometry).Length, await ShaAsync(geometry, ct), new FileInfo(parameters).Length,
            await ShaAsync(parameters, ct));
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

    private async Task<T> RetryAsync<T>(Func<Task<T>> call, CancellationToken ct)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                return await call();
            }
            catch (Exception exception) when (exception is HttpRequestException or IOException or UploadRetryException
                                                  or TaskCanceledException { CancellationToken.IsCancellationRequested: false }
                                              && attempt < _delays.Count && !ct.IsCancellationRequested)
            {
                logger.LogWarning("Загрузка слепка: {Error} — повтор через {Delay} с", exception.Message, _delays[attempt].TotalSeconds);
                await Task.Delay(_delays[attempt], ct);
            }
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

    /// <summary>Строки загрузки в журнал задания; ход — не чаще раза в 5 с.</summary>
    private sealed class UploadLog(Guid jobId, int slot, IJobReporter reporter)
    {
        private DateTimeOffset _lastProgress = DateTimeOffset.MinValue;

        public Task WriteAsync(string text) => reporter.ProgressAsync(new JobProgressDto(jobId, slot, [new JobLogDto(DateTimeOffset.UtcNow, Stage, text)]));

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
