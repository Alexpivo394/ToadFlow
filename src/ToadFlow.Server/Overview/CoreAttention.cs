using Microsoft.EntityFrameworkCore;
using ToadFlow.Contracts;
using ToadFlow.Server.Data;
using ToadFlow.Server.Modules;
using ToadFlow.Server.Settings;

namespace ToadFlow.Server.Overview;

/// <summary>Строки «Требует внимания» от ядра: агенты не в сети, заявки, ошибки за сутки, место на дисках, молчащие рабочие процессы.</summary>
public sealed class CoreAttention(ToadFlowDb db, ISettingsStore settings, ModuleSet modules, ServerStatsReader stats, TimeProvider time)
{
    /// <summary>Мало места: свободно меньше этой доли тома…</summary>
    public const double LowDiskShare = 0.10;

    /// <summary>…или меньше стольких байт.</summary>
    public const long LowDiskBytes = 5L * 1024 * 1024 * 1024;

    public async Task<IReadOnlyList<AttentionItem>> ListAsync(CancellationToken ct)
    {
        var now = time.GetUtcNow();
        var items = new List<AttentionItem>();
        items.AddRange(await AgentsOfflineAsync(now, ct));
        items.AddRange(await RegistrationsAsync(ct));
        items.AddRange(await FailedJobsAsync(now, ct));
        items.AddRange(LowDisk());
        items.AddRange(SilentWorkers(now));
        return items;
    }

    private async Task<IEnumerable<AttentionItem>> AgentsOfflineAsync(DateTimeOffset now, CancellationToken ct)
    {
        var threshold = TimeSpan.FromMinutes((await settings.GetAsync(ct)).AgentsOfflineMinutes);
        var agents = await db.Agents.AsNoTracking().Where(a => a.State == AgentState.Offline)
            .OrderBy(a => a.MachineName).Select(a => new { a.MachineName, Since = a.LastSeenAt ?? a.RegisteredAt }).ToListAsync(ct);
        return agents.Where(a => now - a.Since > threshold)
            .Select(a => new AttentionItem(AttentionLevel.Problem, $"Агент {a.MachineName} не в сети {Russian.Duration(now - a.Since)}", "/farm/agents", null));
    }

    private async Task<IEnumerable<AttentionItem>> RegistrationsAsync(CancellationToken ct)
    {
        var pending = await db.Users.CountAsync(u => u.IsPending, ct);
        return pending == 0
            ? []
            : [new AttentionItem(AttentionLevel.Todo, $"{pending} {Russian.Plural(pending, "заявка", "заявки", "заявок")} на регистрацию", "/admin/users", null,
                UserRole.Operator)];
    }

    private async Task<IEnumerable<AttentionItem>> FailedJobsAsync(DateTimeOffset now, CancellationToken ct)
    {
        var dayAgo = now.AddDays(-1);
        var byKind = await db.Jobs.AsNoTracking().Where(j => j.Status == JobStatus.Failed && j.FinishedAt >= dayAgo)
            .GroupBy(j => j.ModuleKind).Select(g => new { Kind = g.Key, Count = g.Count() }).ToListAsync(ct);
        return modules.Enabled
            .Select(module => (Module: module, Count: byKind.Where(k => module.Kinds.Contains(k.Kind)).Sum(k => k.Count)))
            .Where(m => m.Count > 0)
            .Select(m => new AttentionItem(AttentionLevel.Problem,
                $"{m.Module.Title}: {m.Count} {Russian.Plural(m.Count, "модель", "модели", "моделей")} с ошибкой за сутки",
                $"{m.Module.HistoryPath}?status=Failed", m.Module.Name));
    }

    private IEnumerable<AttentionItem> LowDisk() => stats.Volumes()
        .Where(v => v.FreeBytes < LowDiskBytes || v.FreeBytes < v.TotalBytes * LowDiskShare)
        .Select(v => new AttentionItem(AttentionLevel.Problem, $"Мало места: {v.Name}, свободно {Russian.Gigabytes(v.FreeBytes)}", "/admin/server", null));

    private IEnumerable<AttentionItem> SilentWorkers(DateTimeOffset now) => stats.Workers()
        .Select(w => (Worker: w, Silent: ServerStatsReader.SilentFor(w, now)))
        .Where(w => w.Silent > ServerStatsReader.WorkerSilence)
        .Select(w => new AttentionItem(AttentionLevel.Problem, $"{w.Worker.Title} не на связи {Russian.Duration(w.Silent)}", "/admin/server", w.Worker.Module));
}

/// <summary>Числа и сроки по-русски для строк «Обзора».</summary>
public static class Russian
{
    /// <summary>1 модель, 2 модели, 5 моделей, 21 модель.</summary>
    public static string Plural(int n, string one, string few, string many)
    {
        var tens = Math.Abs(n) % 100;
        var units = tens % 10;
        return tens is >= 11 and <= 14 ? many : units == 1 ? one : units is >= 2 and <= 4 ? few : many;
    }

    /// <summary>«выгружена 1 модель», «выгружено 3 модели»: глагол при «1» (21, 31…) — в роде слова, иначе — средний.</summary>
    public static string Counted(int n, string verbOne, string verbMany, string one, string few, string many) =>
        $"{(Plural(n, "1", "", "") == "1" ? verbOne : verbMany)} {n} {Plural(n, one, few, many)}";

    /// <summary>Сколько времени прошло, грубо: «25 мин», «3 ч», «2 д».</summary>
    public static string Duration(TimeSpan span) =>
        span.TotalMinutes < 60 ? $"{Math.Max(1, (int)span.TotalMinutes)} мин"
        : span.TotalHours < 48 ? $"{(int)span.TotalHours} ч"
        : $"{(int)span.TotalDays} д";

    public static string Gigabytes(long bytes)
    {
        var gb = bytes / (1024.0 * 1024 * 1024);
        return gb >= 10 ? $"{gb:0} ГБ" : $"{gb:0.#} ГБ";
    }
}
