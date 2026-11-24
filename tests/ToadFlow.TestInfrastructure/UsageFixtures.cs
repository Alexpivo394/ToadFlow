using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ToadFlow.Server.Tests.Infrastructure;

/// <summary>Записи пачки аналитики (<c>POST /api/usage/events</c>) для тестов: значения по умолчанию правдоподобны, любое можно заменить.</summary>
public static class UsageFixtures
{
    public const string Machine1 = "WS-01";

    private static JsonObject Node(object value) => JsonSerializer.SerializeToNode(value, TestJson.Options)!.AsObject();

    public static JsonObject Machine(string name = Machine1, DateTimeOffset? at = null, string toadTools = "4.6.0", int cores = 8, int ramMb = 32768,
        string diskKind = "Ssd", int[]? revitYears = null) => Node(new
    {
        type = "machine",
        name,
        cpu = "Intel Core i7-12700",
        cores,
        ramMb,
        gpu = "NVIDIA RTX A2000",
        gpuMemoryMb = 6144,
        diskKind,
        diskTotalGb = 512,
        diskFreeGb = 120,
        os = "Windows 11 Pro 23H2",
        revitYears = revitYears ?? [2022, 2024],
        toadTools,
        at = at ?? DateTimeOffset.UtcNow
    });

    public static JsonObject Session(Guid? id = null, string machine = Machine1, DateTimeOffset? startedAt = null, DateTimeOffset? seenAt = null,
        string end = "Running", int activeSeconds = 600, int revitYear = 2024, string toadTools = "4.6.0", string windowsUser = "ivanov",
        int peakMemoryMb = 4096)
    {
        var started = startedAt ?? DateTimeOffset.UtcNow.AddHours(-1);
        return Node(new
        {
            type = "session",
            id = id ?? Guid.NewGuid(),
            machine,
            windowsUser,
            revitYear,
            revitBuild = "24.3.0.24",
            toadTools,
            startedAt = started,
            seenAt = seenAt ?? started.AddMinutes(30),
            activeSeconds,
            end,
            peakMemoryMb
        });
    }

    public static JsonObject SessionDay(Guid sessionId, DateOnly day, int activeSeconds, string machine = Machine1) => Node(new
    {
        type = "sessionDay",
        sessionId,
        machine,
        day = day.ToString("yyyy-MM-dd"),
        activeSeconds
    });

    public static JsonObject Operation(Guid? id = null, string kind = "Open", DateTimeOffset? startedAt = null, long durationMs = 10_000,
        string outcome = "Succeeded", string modelPath = "RSN://srv01/Проект/АР.rvt", string machine = Machine1, Guid? sessionId = null,
        string? hostModelPath = null, int revitYear = 2024, int? freeMemoryMb = 8000, long? fileSizeBytes = null, string? storage = null,
        string? serverHost = null)
    {
        var name = modelPath.Split('/', '\\').Last();
        var resolvedStorage = storage ?? (modelPath.StartsWith("RSN://", StringComparison.OrdinalIgnoreCase) ? "RevitServer" : "File");
        var host = serverHost ?? (resolvedStorage == "RevitServer" ? modelPath[6..].Split('/')[0] : null);
        return Node(new
        {
            type = "operation",
            id = id ?? Guid.NewGuid(),
            sessionId = sessionId ?? Guid.NewGuid(),
            machine,
            kind,
            startedAt = startedAt ?? DateTimeOffset.UtcNow.AddHours(-1),
            durationMs,
            outcome,
            modelName = name,
            modelPath,
            storage = resolvedStorage,
            serverHost = host,
            hostModelPath,
            fileSizeBytes,
            freeMemoryMb,
            revitYear
        });
    }

    public static JsonObject CommandDay(Guid sessionId, DateOnly day, string commandId = "Export", string name = "Выгрузка", string source = "ToadTools",
        int count = 1, int failed = 0, int cancelled = 0, long durationMs = 1000, string? tab = "ToadTools", string machine = Machine1) => Node(new
    {
        type = "commandDay",
        sessionId,
        machine,
        day = day.ToString("yyyy-MM-dd"),
        commandId,
        name,
        source,
        tab,
        count,
        failed,
        cancelled,
        durationMs
    });

    public static object Batch(params JsonObject[] items) => new { version = 1, items };

    /// <summary>Отправляет пачку и требует успеха; возвращает ответ приёма.</summary>
    public static async Task<IngestAnswer> SendAsync(HttpClient client, params JsonObject[] items)
    {
        var response = await client.PostAsJsonAsync("/api/usage/events", Batch(items), TestJson.Options);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<IngestAnswer>(TestJson.Options))!;
    }

    public sealed record IngestAnswer(int Accepted, int Skipped);

    /// <summary>Id учётки, под которой вошёл клиент.</summary>
    public static async Task<Guid> UserIdAsync(HttpClient client) =>
        (await client.GetJsonAsync<JsonElement>("/api/profile")).GetProperty("id").GetGuid();

    /// <summary>Сегодняшний день в поясе по умолчанию («Общие» — Москва).</summary>
    public static DateOnly Today => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, Zone).DateTime);

    public static TimeZoneInfo Zone => TimeZoneInfo.FindSystemTimeZoneById("Europe/Moscow");

    /// <summary>Начало местного дня — момент UTC.</summary>
    public static DateTimeOffset StartOf(DateOnly day)
    {
        var local = day.ToDateTime(TimeOnly.MinValue);
        return new DateTimeOffset(local, Zone.GetUtcOffset(local)).ToUniversalTime();
    }

    public static string Day(DateOnly day) => day.ToString("yyyy-MM-dd");
}
