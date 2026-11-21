using Microsoft.EntityFrameworkCore;
using ToadFlow.Contracts;
using ToadFlow.Server.Agents;
using ToadFlow.Server.Data;
using ToadFlow.Server.Modules;

namespace ToadFlow.Server.Overview;

/// <summary>Час на графике «Задания за сутки»: начало часа (UTC) и сколько заданий закончилось в нём.</summary>
public sealed record HourPoint(DateTimeOffset Hour, int Succeeded, int Warning, int Failed);

/// <summary>Пульс системы для шапки «Обзора»: работа за сутки, очередь, занятые Revit, задания по часам.</summary>
/// <param name="DoneDay">Готовых (в том числе с предупреждением) заданий за 24 часа.</param>
/// <param name="Hourly">Ровно 24 часа, последний — текущий.</param>
public sealed record OverviewPulse(int DoneDay, int FailedDay, int Queued, int RevitBusy, int RevitTotal, IReadOnlyList<HourPoint> Hourly);

/// <summary>Считает пульс по заданиям включённых модулей и экземплярам Revit агентов на связи.</summary>
public sealed class OverviewPulseReader(ToadFlowDb db, ModuleSet modules, AgentRegistry registry, TimeProvider time)
{
    private const int Hours = 24;

    public async Task<OverviewPulse> ReadAsync(CancellationToken ct)
    {
        var now = time.GetUtcNow().ToUniversalTime();
        var currentHour = new DateTimeOffset(now.Year, now.Month, now.Day, now.Hour, 0, 0, TimeSpan.Zero);
        var firstHour = currentHour.AddHours(-(Hours - 1));
        var dayAgo = now.AddHours(-Hours);
        var since = dayAgo < firstHour ? dayAgo : firstHour;

        var kinds = modules.EnabledKinds;
        var jobs = db.Jobs.AsNoTracking().Where(j => kinds.Contains(j.ModuleKind));
        var finished = await jobs
            .Where(j => j.FinishedAt >= since && (j.Status == JobStatus.Succeeded || j.Status == JobStatus.Warning || j.Status == JobStatus.Failed))
            .Select(j => new { At = j.FinishedAt!.Value, j.Status }).ToListAsync(ct);
        var queued = await jobs.CountAsync(j => j.Status == JobStatus.Queued, ct);

        var hourly = Enumerable.Range(0, Hours).Select(i =>
        {
            var start = firstHour.AddHours(i);
            var inHour = finished.Where(j => j.At >= start && j.At < start.AddHours(1)).ToList();
            return new HourPoint(start, inHour.Count(j => j.Status == JobStatus.Succeeded), inHour.Count(j => j.Status == JobStatus.Warning),
                inHour.Count(j => j.Status == JobStatus.Failed));
        }).ToList();
        var lastDay = finished.Where(j => j.At >= dayAgo).ToList();

        var instances = registry.Online().SelectMany(registry.Instances).ToList();
        return new OverviewPulse(lastDay.Count(j => j.Status != JobStatus.Failed), lastDay.Count(j => j.Status == JobStatus.Failed), queued,
            instances.Count(i => i.State is InstanceState.Busy or InstanceState.Unresponsive), instances.Count, hourly);
    }
}
