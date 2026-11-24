using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using ToadFlow.Server.Data;

namespace ToadFlow.Server.Usage.Ingest;

/// <summary>
///     Сохраняет пачку от ToadTools. Каждая запись разбирается отдельно: неверная пропускается, остальные сохраняются. Вся пачка
///     уходит в базу одним пакетом команд <c>INSERT … ON CONFLICT</c>: разовые записи (операции) не задваиваются, нарастающие
///     итоги (сессия, день сессии, команда за день) заменяются и не уменьшаются.
/// </summary>
public sealed class UsageIngest(ToadFlowDb db, TimeProvider time)
{
    /// <summary>Насколько время записи может опережать часы сервера: часы машин немного врут.</summary>
    public static readonly TimeSpan FutureTolerance = TimeSpan.FromHours(1);

    /// <summary>Дольше суток операция не длится: такое значение — сбой часов или сборщика.</summary>
    public const long MaxDurationMs = 24L * 60 * 60 * 1000;

    public async Task<IngestResult> ApplyAsync(Guid userId, IngestBatch batch, CancellationToken ct)
    {
        var now = time.GetUtcNow();
        var commands = new List<(NpgsqlBatchCommand Command, bool CountsRows)>();
        var skipped = 0;
        foreach (var item in batch.Items)
        {
            var built = Build(item, userId, now);
            if (built.Count == 0) skipped++;
            else commands.AddRange(built);
        }

        if (commands.Count == 0) return new IngestResult(0, skipped);

        var connection = (NpgsqlConnection)db.Database.GetDbConnection();
        await db.Database.OpenConnectionAsync(ct);
        try
        {
            await using var npgsqlBatch = new NpgsqlBatch(connection);
            foreach (var (command, _) in commands) npgsqlBatch.BatchCommands.Add(command);
            await npgsqlBatch.ExecuteNonQueryAsync(ct);
        }
        finally
        {
            await db.Database.CloseConnectionAsync();
        }

        // Первая команда записи — главная; у операции 0 строк значит «уже была».
        var duplicates = commands.Count(c => c.CountsRows && c.Command.RecordsAffected == 0);
        var accepted = batch.Items.Count - skipped - duplicates;
        return new IngestResult(accepted, skipped + duplicates);
    }

    /// <summary>Команды одной записи; пусто — запись пропускается.</summary>
    private static List<(NpgsqlBatchCommand, bool)> Build(JsonElement item, Guid userId, DateTimeOffset now)
    {
        if (item.ValueKind != JsonValueKind.Object) return [];
        var r = new ItemReader(item, now);
        try
        {
            return r.String("type") switch
            {
                "machine" => Machine(r, userId),
                "session" => Session(r, userId, now),
                "sessionDay" => SessionDay(r, userId),
                "operation" => Operation(r, userId, now),
                "commandDay" => CommandDay(r, userId),
                _ => []
            };
        }
        catch (InvalidItemException)
        {
            return [];
        }
    }

    private static List<(NpgsqlBatchCommand, bool)> Machine(ItemReader r, Guid userId)
    {
        var name = r.Required("name", 100);
        var at = r.Time("at");
        var diskKind = r.OneOf("diskKind", UsageValues.DiskKinds) ?? "Unknown";
        var command = Command("""
            INSERT INTO usage_machines (key, name, cpu, cores, ram_mb, gpu, gpu_memory_mb, disk_kind, disk_total_gb, disk_free_gb, os, revit_years,
                                        toad_tools, first_seen_at, last_seen_at, last_user_id)
            VALUES ($1, $2, $3, $4, $5, $6, $7, $8, $9, $10, $11, $12, $13, $14, $14, $15)
            ON CONFLICT (key) DO UPDATE SET
                name = excluded.name, cpu = excluded.cpu, cores = excluded.cores, ram_mb = excluded.ram_mb, gpu = excluded.gpu,
                gpu_memory_mb = excluded.gpu_memory_mb, disk_kind = excluded.disk_kind, disk_total_gb = excluded.disk_total_gb,
                disk_free_gb = excluded.disk_free_gb, os = excluded.os, revit_years = excluded.revit_years, toad_tools = excluded.toad_tools,
                last_user_id = excluded.last_user_id,
                first_seen_at = LEAST(usage_machines.first_seen_at, excluded.first_seen_at),
                last_seen_at = GREATEST(usage_machines.last_seen_at, excluded.last_seen_at)
            """, UsageValues.MachineKey(name), name, r.Text("cpu", 200), r.Int("cores"), r.Int("ramMb"), r.Text("gpu", 200), r.Int("gpuMemoryMb"),
            diskKind, r.Int("diskTotalGb"), r.Int("diskFreeGb"), r.Text("os", 200), r.Years("revitYears"), r.Text("toadTools", 50), at, userId);
        return [(command, false)];
    }

    private static List<(NpgsqlBatchCommand, bool)> Session(ItemReader r, Guid userId, DateTimeOffset now)
    {
        var id = r.Guid("id");
        var machine = UsageValues.MachineKey(r.Required("machine", 100));
        var startedAt = r.Time("startedAt");
        var seenAt = r.Time("seenAt");
        if (seenAt < startedAt) throw new InvalidItemException();
        var active = r.Int("activeSeconds") ?? 0;
        if (active < 0) throw new InvalidItemException();
        var end = r.OneOf("end", UsageValues.SessionEnds) ?? throw new InvalidItemException();
        var session = Command("""
            INSERT INTO usage_sessions (id, user_id, machine_key, windows_user, revit_year, revit_build, toad_tools, started_at, seen_at,
                                        active_seconds, "end", peak_memory_mb, received_at)
            VALUES ($1, $2, $3, $4, $5, $6, $7, $8, $9, $10, $11, $12, $13)
            ON CONFLICT (id) DO UPDATE SET
                user_id = excluded.user_id, machine_key = excluded.machine_key, windows_user = excluded.windows_user,
                revit_year = excluded.revit_year, revit_build = excluded.revit_build, toad_tools = excluded.toad_tools,
                started_at = excluded.started_at, seen_at = excluded.seen_at,
                active_seconds = GREATEST(usage_sessions.active_seconds, excluded.active_seconds), "end" = excluded."end",
                peak_memory_mb = GREATEST(usage_sessions.peak_memory_mb, excluded.peak_memory_mb), received_at = excluded.received_at
            WHERE usage_sessions.seen_at < excluded.seen_at OR (usage_sessions.seen_at = excluded.seen_at AND usage_sessions."end" = 'Running')
            """, id, userId, machine, r.Text("windowsUser", 100), r.Int("revitYear") ?? 0, r.Text("revitBuild", 50), r.Text("toadTools", 50),
            startedAt, seenAt, active, end, r.Int("peakMemoryMb"), now);
        var touch = Command("UPDATE usage_machines SET last_seen_at = GREATEST(last_seen_at, $2) WHERE key = $1", machine, seenAt);
        return [(session, false), (touch, false)];
    }

    private static List<(NpgsqlBatchCommand, bool)> SessionDay(ItemReader r, Guid userId)
    {
        var active = r.Int("activeSeconds") ?? throw new InvalidItemException();
        if (active is < 0 or > 24 * 60 * 60) throw new InvalidItemException();
        var command = Command("""
            INSERT INTO usage_session_days (session_id, day, user_id, machine_key, active_seconds) VALUES ($1, $2, $3, $4, $5)
            ON CONFLICT (session_id, day) DO UPDATE SET
                user_id = excluded.user_id, machine_key = excluded.machine_key,
                active_seconds = GREATEST(usage_session_days.active_seconds, excluded.active_seconds)
            """, r.Guid("sessionId"), r.Day("day"), userId, UsageValues.MachineKey(r.Required("machine", 100)), active);
        return [(command, false)];
    }

    private static List<(NpgsqlBatchCommand, bool)> Operation(ItemReader r, Guid userId, DateTimeOffset now)
    {
        var duration = r.Long("durationMs") ?? throw new InvalidItemException();
        if (duration is < 0 or > MaxDurationMs) throw new InvalidItemException();
        var kind = r.OneOf("kind", UsageValues.OperationKinds) ?? throw new InvalidItemException();
        var outcome = r.OneOf("outcome", UsageValues.Outcomes) ?? throw new InvalidItemException();
        var path = r.Required("modelPath", 1000);
        var name = r.Text("modelName", 260) ?? path.Split('/', '\\').Last();
        var storage = r.OneOf("storage", UsageValues.Storages) ?? StorageOf(path);
        var host = r.Text("serverHost", 100) ?? (storage == "RevitServer" ? HostOf(path) : null);
        var hostModel = r.Text("hostModelPath", 1000);
        var command = Command("""
            INSERT INTO usage_operations (id, user_id, session_id, machine_key, kind, started_at, duration_ms, outcome, model_name, model_path,
                                          model_key, storage, server_host, host_model_key, file_size_bytes, free_memory_mb, revit_year, received_at)
            VALUES ($1, $2, $3, $4, $5, $6, $7, $8, $9, $10, $11, $12, $13, $14, $15, $16, $17, $18)
            ON CONFLICT (id) DO NOTHING
            """, r.Guid("id"), userId, r.Guid("sessionId"), UsageValues.MachineKey(r.Required("machine", 100)), kind, r.Time("startedAt"), duration,
            outcome, name, path, UsageValues.ModelKey(path), storage, host?.ToLowerInvariant(), hostModel is null ? null : UsageValues.ModelKey(hostModel),
            r.Long("fileSizeBytes"), r.Int("freeMemoryMb"), r.Int("revitYear") ?? 0, now);
        return [(command, true)];
    }

    private static List<(NpgsqlBatchCommand, bool)> CommandDay(ItemReader r, Guid userId)
    {
        var source = r.OneOf("source", UsageValues.CommandSources) ?? throw new InvalidItemException();
        var count = r.Int("count") ?? throw new InvalidItemException();
        var failed = r.Int("failed") ?? 0;
        var cancelled = r.Int("cancelled") ?? 0;
        var duration = r.Long("durationMs") ?? 0;
        if (count < 0 || failed < 0 || cancelled < 0 || duration < 0) throw new InvalidItemException();
        var commandId = r.Required("commandId", 200);
        var command = Command("""
            INSERT INTO usage_command_days (session_id, day, source, command_id, user_id, machine_key, name, tab, count, failed, cancelled, duration_ms)
            VALUES ($1, $2, $3, $4, $5, $6, $7, $8, $9, $10, $11, $12)
            ON CONFLICT (session_id, day, source, command_id) DO UPDATE SET
                user_id = excluded.user_id, machine_key = excluded.machine_key, name = excluded.name, tab = excluded.tab,
                count = GREATEST(usage_command_days.count, excluded.count), failed = GREATEST(usage_command_days.failed, excluded.failed),
                cancelled = GREATEST(usage_command_days.cancelled, excluded.cancelled),
                duration_ms = GREATEST(usage_command_days.duration_ms, excluded.duration_ms)
            """, r.Guid("sessionId"), r.Day("day"), source, commandId, userId, UsageValues.MachineKey(r.Required("machine", 100)),
            r.Text("name", 200) ?? commandId, r.Text("tab", 100), count, failed, cancelled, duration);
        return [(command, false)];
    }

    internal static string StorageOf(string path) =>
        path.StartsWith("rsn://", StringComparison.OrdinalIgnoreCase) ? "RevitServer"
        : path.StartsWith("bim 360://", StringComparison.OrdinalIgnoreCase) || path.StartsWith("autodesk docs://", StringComparison.OrdinalIgnoreCase)
          || path.StartsWith("cld://", StringComparison.OrdinalIgnoreCase) ? "Cloud"
        : "File";

    private static string? HostOf(string path)
    {
        var rest = path["rsn://".Length..];
        var slash = rest.IndexOf('/');
        return slash > 0 ? rest[..slash] : null;
    }

    private static NpgsqlBatchCommand Command(string sql, params object?[] values)
    {
        var command = new NpgsqlBatchCommand(sql);
        foreach (var value in values) command.Parameters.Add(new NpgsqlParameter { Value = value ?? DBNull.Value });
        return command;
    }

    private sealed class InvalidItemException : Exception;

    /// <summary>Чтение полей записи: обязательное поле, которого нет или оно неверное, бросает <see cref="InvalidItemException" />.</summary>
    private readonly struct ItemReader(JsonElement item, DateTimeOffset now)
    {
        private JsonElement? Get(string name) =>
            item.TryGetProperty(name, out var value) && value.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined) ? value : null;

        public string? String(string name) => Get(name) is { ValueKind: JsonValueKind.String } value ? value.GetString() : null;

        public string? Text(string name, int max) =>
            String(name)?.Trim() is { Length: > 0 } text ? text.Length > max ? text[..max] : text : null;

        public string Required(string name, int max) => Text(name, max) ?? throw new InvalidItemException();

        public string? OneOf(string name, string[] allowed) => String(name) is { } value
            ? allowed.FirstOrDefault(a => a.Equals(value, StringComparison.OrdinalIgnoreCase)) ?? throw new InvalidItemException()
            : null;

        public int? Int(string name) => Get(name) switch
        {
            null => null,
            { ValueKind: JsonValueKind.Number } value when value.TryGetInt64(out var number) => number is >= int.MinValue and <= int.MaxValue
                ? (int)number
                : throw new InvalidItemException(),
            _ => throw new InvalidItemException()
        };

        public long? Long(string name) => Get(name) switch
        {
            null => null,
            { ValueKind: JsonValueKind.Number } value when value.TryGetInt64(out var number) => number,
            _ => throw new InvalidItemException()
        };

        public Guid Guid(string name) =>
            Get(name) is { ValueKind: JsonValueKind.String } value && value.TryGetGuid(out var id) && id != System.Guid.Empty
                ? id
                : throw new InvalidItemException();

        /// <summary>Время UTC; из будущего дальше допуска — запись неверна.</summary>
        public DateTimeOffset Time(string name)
        {
            if (Get(name) is not { ValueKind: JsonValueKind.String } value || !value.TryGetDateTimeOffset(out var at)) throw new InvalidItemException();
            at = at.ToUniversalTime();
            return at > now + FutureTolerance ? throw new InvalidItemException() : at;
        }

        /// <summary>Местный день машины; дальше завтрашнего по UTC быть не может.</summary>
        public DateOnly Day(string name)
        {
            if (String(name) is not { } text || !DateOnly.TryParseExact(text, "yyyy-MM-dd", out var day)) throw new InvalidItemException();
            return day > DateOnly.FromDateTime(now.UtcDateTime).AddDays(1) ? throw new InvalidItemException() : day;
        }

        public int[] Years(string name) => Get(name) is { ValueKind: JsonValueKind.Array } value
            ? value.EnumerateArray().Where(y => y.ValueKind == JsonValueKind.Number && y.TryGetInt32(out _)).Select(y => y.GetInt32())
                .Where(y => y is >= 2000 and < 2100).Distinct().Order().ToArray()
            : [];
    }
}
