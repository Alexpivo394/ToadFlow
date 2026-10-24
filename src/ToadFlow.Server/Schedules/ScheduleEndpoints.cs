using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using ToadFlow.Contracts;
using ToadFlow.Server.Agents;
using ToadFlow.Server.Auth;
using ToadFlow.Server.Data;
using ToadFlow.Server.Data.Entities;
using ToadFlow.Server.Events;
using ToadFlow.Server.Modules;
using ToadFlow.Server.Settings;

namespace ToadFlow.Server.Schedules;

public sealed record ScheduleRequest(
    string Name,
    string ModuleKind,
    Guid SourceId,
    string OutputFolderTemplate,
    SelectionDto Selection,
    JsonElement ModuleSettings,
    RecurrenceKind RecurrenceKind,
    int DaysOfWeekMask,
    TimeOnly TimeOfDay,
    int? IntervalHours,
    DateTimeOffset? OnceAt,
    int Priority,
    int? ModelTimeoutMinutes,
    int MaxRetries,
    bool IsEnabled);

public sealed record LastRunView(Guid Id, RunState State, DateTimeOffset CreatedAt, int Succeeded, int Failed, int Total);

public sealed record ScheduleView(
    Guid Id,
    string Name,
    string ModuleKind,
    Guid SourceId,
    string SourceName,
    int? SourceRevitYear,
    string OutputFolderTemplate,
    SelectionDto Selection,
    JsonElement ModuleSettings,
    RecurrenceKind RecurrenceKind,
    int DaysOfWeekMask,
    TimeOnly TimeOfDay,
    int? IntervalHours,
    DateTimeOffset? OnceAt,
    int Priority,
    int ModelTimeoutMinutes,
    int MaxRetries,
    bool IsEnabled,
    DateTimeOffset? NextRunAt,
    int ModelCount,
    LastRunView? LastRun,
    string CreatedBy,
    DateTimeOffset CreatedAt,
    string UpdatedBy,
    DateTimeOffset UpdatedAt,
    IReadOnlyList<string> Warnings);

public sealed record ScheduleChangeView(DateTimeOffset At, string UserName, string Summary);

public sealed record EnabledRequest(bool IsEnabled);

public static class ScheduleEndpoints
{
    public static void MapSchedules(this RouteGroupBuilder api)
    {
        var schedules = api.MapGroup("/schedules");
        schedules.MapGet("/", async (ToadFlowDb db) => await ViewsAsync(db, null));
        schedules.MapGet("/{id:guid}", async (Guid id, ToadFlowDb db) =>
            (await ViewsAsync(db, id)).SingleOrDefault() is { } view ? Results.Ok(view) : Results.NotFound());
        schedules.MapGet("/{id:guid}/changes", async (Guid id, ToadFlowDb db) =>
            await db.ScheduleChanges.Where(c => c.ScheduleId == id).OrderByDescending(c => c.At)
                .Join(db.Users, c => c.UserId, u => u.Id, (c, u) => new ScheduleChangeView(c.At, u.DisplayName, c.Summary))
                .ToListAsync());

        var write = schedules.MapGroup("").RequireAuthorization(Policies.Operator);
        write.MapPost("/", (ScheduleRequest request, HttpContext http, IServiceProvider services) => SaveAsync(null, request, http, services));
        write.MapPut("/{id:guid}", (Guid id, ScheduleRequest request, HttpContext http, IServiceProvider services) => SaveAsync(id, request, http, services));
        write.MapPost("/{id:guid}/enabled", SetEnabledAsync);
        write.MapDelete("/{id:guid}", DeleteAsync);
        write.MapPost("/{id:guid}/run", async (Guid id, ToadFlowDb db, RunFactory runs, HttpContext http) =>
        {
            if (!await db.Schedules.AnyAsync(s => s.Id == id)) return Results.NotFound();
            var run = await runs.StartAsync(id, http.User.UserId(), http.User.Actor());
            return Results.Ok(new { runId = run.Id });
        });
    }

    private static async Task<IResult> SaveAsync(Guid? id, ScheduleRequest request, HttpContext http, IServiceProvider services)
    {
        var db = services.GetRequiredService<ToadFlowDb>();
        var time = services.GetRequiredService<TimeProvider>();
        var settings = await services.GetRequiredService<ISettingsStore>().GetAsync();

        var source = await db.Sources.AsNoTracking().SingleOrDefaultAsync(s => s.Id == request.SourceId);
        var errors = Validate(request, source);
        if (services.GetRequiredService<ServerModuleRegistry>().Find(request.ModuleKind) is not { } module) errors.Add($"Неизвестный вид задачи «{request.ModuleKind}»");
        else errors.AddRange(await module.ValidateSettingsAsync(request.ModuleSettings, services, http.RequestAborted));
        if (errors.Count > 0) return Results.BadRequest(new { error = string.Join("; ", errors), errors });

        var now = time.GetUtcNow();
        var userId = http.User.UserId();
        Schedule schedule;
        Schedule? before = null;
        string? oldSourceName = null;
        if (id is null)
        {
            schedule = new Schedule { Id = Guid.CreateVersion7(), CreatedByUserId = userId, CreatedAt = now };
            db.Schedules.Add(schedule);
        }
        else
        {
            schedule = await db.Schedules.SingleOrDefaultAsync(s => s.Id == id) ?? null!;
            if (schedule is null) return Results.NotFound();
            before = Copy(schedule);
            oldSourceName = await db.Sources.Where(s => s.Id == schedule.SourceId).Select(s => s.Name).SingleOrDefaultAsync();
        }

        Apply(schedule, request, settings.DefaultModelTimeoutMinutes);
        schedule.UpdatedByUserId = userId;
        schedule.UpdatedAt = now;
        schedule.NextRunAt = schedule.IsEnabled ? Recurrence.Next(RecurrenceRule.Of(schedule), now, settings.TimeZone) : null;

        var summary = before is null ? "создано" : ScheduleChangeSummary.Describe(before, schedule, oldSourceName, source!.Name);
        db.ScheduleChanges.Add(new ScheduleChange { Id = Guid.CreateVersion7(), ScheduleId = schedule.Id, At = now, UserId = userId, Summary = summary });
        await db.SaveChangesAsync();
        services.GetRequiredService<IDomainEvents>().Publish(new ScheduleChanged(http.User.Actor(), schedule.Id, schedule.Name, summary));

        var view = (await ViewsAsync(db, schedule.Id)).Single() with { Warnings = await WarningsAsync(db, schedule, source!) };
        return id is null ? Results.Created($"/api/schedules/{schedule.Id}", view) : Results.Ok(view);
    }

    private static async Task<IResult> SetEnabledAsync(Guid id, EnabledRequest request, ToadFlowDb db, ISettingsStore settings, TimeProvider time,
        HttpContext http, IDomainEvents events)
    {
        var schedule = await db.Schedules.FindAsync(id);
        if (schedule is null) return Results.NotFound();

        var now = time.GetUtcNow();
        schedule.IsEnabled = request.IsEnabled;
        schedule.NextRunAt = request.IsEnabled ? Recurrence.Next(RecurrenceRule.Of(schedule), now, (await settings.GetAsync()).TimeZone) : null;
        schedule.UpdatedByUserId = http.User.UserId();
        schedule.UpdatedAt = now;
        var summary = request.IsEnabled ? "включено" : "выключено";
        db.ScheduleChanges.Add(new ScheduleChange { Id = Guid.CreateVersion7(), ScheduleId = id, At = now, UserId = schedule.UpdatedByUserId, Summary = summary });
        await db.SaveChangesAsync();
        events.Publish(new ScheduleChanged(http.User.Actor(), id, schedule.Name, summary));
        return Results.NoContent();
    }

    private static async Task<IResult> DeleteAsync(Guid id, ToadFlowDb db, HttpContext http, IDomainEvents events)
    {
        var schedule = await db.Schedules.FindAsync(id);
        if (schedule is null) return Results.NotFound();

        db.Schedules.Remove(schedule);
        await db.SaveChangesAsync();
        events.Publish(new ScheduleChanged(http.User.Actor(), id, schedule.Name, "удалено"));
        return Results.NoContent();
    }

    private static List<string> Validate(ScheduleRequest request, Source? source)
    {
        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(request.Name)) errors.Add("Укажите название");
        if (source is null) errors.Add("Источник не найден");
        if (OutputFolderTemplate.Validate(request.OutputFolderTemplate) is { } folderError) errors.Add(folderError);
        if (request.Selection is null || request.Selection.Models.Length + request.Selection.Folders.Length == 0)
            errors.Add("Отметьте хотя бы одну модель или папку");
        else if (request.Selection.Models.Any(m => !m.Rvt && !m.Nwc) || request.Selection.Folders.Any(f => !f.Rvt && !f.Nwc))
            errors.Add("У каждой отмеченной модели и папки выберите RVT, NWC или оба");

        switch (request.RecurrenceKind)
        {
            case RecurrenceKind.Weekly when (request.DaysOfWeekMask & 0b1111111) == 0:
                errors.Add("Отметьте хотя бы один день недели");
                break;
            case RecurrenceKind.EveryNHours when request.IntervalHours is not (>= 1 and <= 24):
                errors.Add("Интервал — от 1 до 24 часов");
                break;
            case RecurrenceKind.Once when request.OnceAt is null:
                errors.Add("Укажите дату и время запуска");
                break;
        }

        if (request.ModelTimeoutMinutes is < 1) errors.Add("Таймаут на модель — не меньше 1 минуты");
        if (request.MaxRetries is < 0 or > 10) errors.Add("Повторов упавших — от 0 до 10");
        return errors;
    }

    private static void Apply(Schedule schedule, ScheduleRequest request, int defaultTimeout)
    {
        schedule.Name = request.Name.Trim();
        schedule.ModuleKind = request.ModuleKind;
        schedule.SourceId = request.SourceId;
        schedule.OutputFolderTemplate = request.OutputFolderTemplate.Trim();
        schedule.Selection = request.Selection;
        schedule.ModuleSettingsJson = request.ModuleSettings.GetRawText();
        schedule.RecurrenceKind = request.RecurrenceKind;
        schedule.DaysOfWeekMask = request.DaysOfWeekMask & 0b1111111;
        schedule.TimeOfDay = request.TimeOfDay;
        schedule.IntervalHours = request.IntervalHours;
        schedule.OnceAt = request.OnceAt?.ToUniversalTime();
        schedule.Priority = Math.Clamp(request.Priority, -1, 1);
        schedule.ModelTimeoutMinutes = request.ModelTimeoutMinutes ?? (schedule.ModelTimeoutMinutes > 0 ? schedule.ModelTimeoutMinutes : defaultTimeout);
        schedule.MaxRetries = request.MaxRetries;
        schedule.IsEnabled = request.IsEnabled;
    }

    private static async Task<List<string>> WarningsAsync(ToadFlowDb db, Schedule schedule, Source source)
    {
        var warnings = new List<string>();
        if (source.RevitYear is { } year)
        {
            var agents = (await db.Agents.AsNoTracking().Where(a => a.State != AgentState.Disabled).ToListAsync())
                .Where(a => a.Revits.Any(r => r.Year == year)).ToList();
            if (agents.Count == 0)
                warnings.Add($"Для Revit {year} нет ни одного агента — задания будут ждать, пока он появится");
            else if (!agents.Any(a => AgentCapabilities.CanRun(a.AgentVersion, schedule.ModuleKind)))
                warnings.Add($"Агенты с Revit {year} старой версии и не умеют эту задачу — задания будут ждать, пока агента обновят до 1.1 или новее");
        }

        var duplicates = schedule.Selection.Models.GroupBy(m => m.Name, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1)
            .Select(g => g.First().Name).ToList();
        if (duplicates.Count > 0)
            warnings.Add($"Одинаковые имена моделей: {string.Join(", ", duplicates)} — файлы перезапишут друг друга в папке результатов");
        return warnings;
    }

    private static async Task<List<ScheduleView>> ViewsAsync(ToadFlowDb db, Guid? id)
    {
        var query = db.Schedules.AsNoTracking();
        if (id is not null) query = query.Where(s => s.Id == id);
        var schedules = await query.OrderBy(s => s.Name).ToListAsync();
        var ids = schedules.Select(s => s.Id).ToList();

        var users = await db.Users.AsNoTracking().ToDictionaryAsync(u => u.Id, u => u.DisplayName);
        var sources = await db.Sources.AsNoTracking().ToDictionaryAsync(s => s.Id);
        var lastRuns = await db.Runs.AsNoTracking().Where(r => r.ScheduleId != null && ids.Contains(r.ScheduleId.Value))
            .GroupBy(r => r.ScheduleId)
            .Select(g => g.OrderByDescending(r => r.CreatedAt).First())
            .ToListAsync();
        var runIds = lastRuns.Select(r => r.Id).ToList();
        var counts = await db.Jobs.AsNoTracking().Where(j => runIds.Contains(j.RunId))
            .GroupBy(j => j.RunId)
            .Select(g => new
            {
                RunId = g.Key,
                Succeeded = g.Count(j => j.Status == JobStatus.Succeeded || j.Status == JobStatus.Warning),
                Failed = g.Count(j => j.Status == JobStatus.Failed),
                Total = g.Count()
            })
            .ToDictionaryAsync(c => c.RunId);

        return schedules.Select(s =>
        {
            var source = sources.GetValueOrDefault(s.SourceId);
            var last = lastRuns.FirstOrDefault(r => r.ScheduleId == s.Id);
            var count = last is null ? null : counts.GetValueOrDefault(last.Id);
            using var settings = JsonDocument.Parse(s.ModuleSettingsJson);
            return new ScheduleView(s.Id, s.Name, s.ModuleKind, s.SourceId, source?.Name ?? "—", source?.RevitYear, s.OutputFolderTemplate,
                s.Selection, settings.RootElement.Clone(), s.RecurrenceKind, s.DaysOfWeekMask, s.TimeOfDay, s.IntervalHours, s.OnceAt, s.Priority,
                s.ModelTimeoutMinutes, s.MaxRetries, s.IsEnabled, s.NextRunAt,
                s.LastKnownModels?.Count ?? s.Selection.Models.Length,
                last is null ? null : new LastRunView(last.Id, last.State, last.CreatedAt, count?.Succeeded ?? 0, count?.Failed ?? 0, count?.Total ?? 0),
                users.GetValueOrDefault(s.CreatedByUserId, "—"), s.CreatedAt, users.GetValueOrDefault(s.UpdatedByUserId, "—"), s.UpdatedAt, []);
        }).ToList();
    }

    private static Schedule Copy(Schedule s) => new()
    {
        Name = s.Name, SourceId = s.SourceId, OutputFolderTemplate = s.OutputFolderTemplate, Selection = s.Selection,
        ModuleSettingsJson = s.ModuleSettingsJson, RecurrenceKind = s.RecurrenceKind, DaysOfWeekMask = s.DaysOfWeekMask,
        TimeOfDay = s.TimeOfDay, IntervalHours = s.IntervalHours, OnceAt = s.OnceAt, Priority = s.Priority,
        ModelTimeoutMinutes = s.ModelTimeoutMinutes, MaxRetries = s.MaxRetries, IsEnabled = s.IsEnabled
    };
}
