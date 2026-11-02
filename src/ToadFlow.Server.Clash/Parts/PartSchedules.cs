using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using ToadFlow.Contracts;
using ToadFlow.Server.Auth;
using ToadFlow.Server.Data;
using ToadFlow.Server.Data.Entities;

namespace ToadFlow.Server.Clash.Parts;

/// <summary>Расписание выгрузки для коллизий глазами части: выбрано ли оно для её регулярного прогона. Поля повторения — как у расписания.</summary>
public sealed record PartScheduleView(Guid Id, string Name, bool Selected, bool IsEnabled, RecurrenceKind RecurrenceKind, int DaysOfWeekMask, string TimeOfDay,
    int? IntervalHours, DateTimeOffset? OnceAt, DateTimeOffset? NextRunAt);

/// <param name="ScheduleIds">Расписания, которые прогоняют часть; из остальных она убирается.</param>
public sealed record PartSchedulesRequest(IReadOnlyList<Guid> ScheduleIds);

/// <summary>
///     Регулярный прогон части — список частей в настройках расписаний выгрузки для коллизий (<see cref="ClashScheduleSettings.Parts" />).
///     Выбор пишется в настройки расписаний с сохранением прочих полей и в журнал изменений расписания.
/// </summary>
public static class PartSchedules
{
    public static void MapClashPartSchedules(this RouteGroupBuilder api)
    {
        var part = api.MapGroup("/clash/parts/{id:guid}/schedules");
        part.MapGet("", ListAsync);
        part.MapPut("", PutAsync).RequireAuthorization(Policies.Operator);
    }

    /// <summary>Части каждого расписания выгрузки для коллизий; битые настройки — без частей.</summary>
    public static async Task<IReadOnlyList<(Schedule Schedule, IReadOnlyList<Guid> Parts)>> ClashSchedulesAsync(ToadFlowDb db, CancellationToken ct)
    {
        var schedules = await db.Schedules.AsNoTracking().Where(s => s.ModuleKind == ModuleKinds.ClashExtract).OrderBy(s => s.Name).ToListAsync(ct);
        return schedules.Select(s => (s, PartsOf(s.ModuleSettingsJson))).ToList();
    }

    private static async Task<IResult> ListAsync(Guid id, ToadFlowDb db, CancellationToken ct)
    {
        if (!await db.Set<ClashPart>().AnyAsync(p => p.Id == id, ct)) return Results.NotFound();
        return Results.Ok((await ClashSchedulesAsync(db, ct)).Select(x => new PartScheduleView(x.Schedule.Id, x.Schedule.Name, x.Parts.Contains(id),
            x.Schedule.IsEnabled, x.Schedule.RecurrenceKind, x.Schedule.DaysOfWeekMask, x.Schedule.TimeOfDay.ToString("HH:mm"), x.Schedule.IntervalHours,
            x.Schedule.OnceAt, x.Schedule.NextRunAt)).ToList());
    }

    private static async Task<IResult> PutAsync(Guid id, PartSchedulesRequest request, ToadFlowDb db, HttpContext http, TimeProvider time, CancellationToken ct)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var part = await db.Set<ClashPart>().FromSqlInterpolated($"SELECT * FROM clash_parts WHERE id = {id} FOR UPDATE").SingleOrDefaultAsync(ct);
        if (part is null) return Results.NotFound();
        if (await ApplyAsync(db, part, request.ScheduleIds ?? [], http.User.UserId(), time.GetUtcNow(), ct) is { } error)
            return Results.BadRequest(new { error });
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return await ListAsync(id, db, ct);
    }

    /// <summary>
    ///     Часть — в выбранных расписаниях и ни в каких других. Изменения не сохраняет: вызывающий сохраняет в своей транзакции.
    /// </summary>
    /// <returns>Текст ошибки; null — применено.</returns>
    public static async Task<string?> ApplyAsync(ToadFlowDb db, ClashPart part, IReadOnlyList<Guid> scheduleIds, Guid userId, DateTimeOffset now,
        CancellationToken ct)
    {
        if (part.ApprovedVersion is null) return "Сначала утвердите часть";
        var schedules = await db.Schedules.FromSqlInterpolated($"SELECT * FROM schedules WHERE module_kind = {ModuleKinds.ClashExtract} FOR UPDATE")
            .ToListAsync(ct);
        var wanted = scheduleIds.ToHashSet();
        if (wanted.Except(schedules.Select(s => s.Id)).Any()) return "Расписание выгрузки для коллизий не найдено";

        foreach (var schedule in schedules)
        {
            var parts = PartsOf(schedule.ModuleSettingsJson).ToList();
            var selected = wanted.Contains(schedule.Id);
            if (selected == parts.Contains(part.Id)) continue;
            if (selected) parts.Add(part.Id);
            else parts.Remove(part.Id);

            schedule.ModuleSettingsJson = WithParts(schedule.ModuleSettingsJson, parts);
            schedule.UpdatedAt = now;
            schedule.UpdatedByUserId = userId;
            db.ScheduleChanges.Add(new ScheduleChange
            {
                Id = Guid.NewGuid(),
                ScheduleId = schedule.Id,
                At = now,
                UserId = userId,
                Summary = selected ? $"Часть «{part.Name}»: добавлена в регулярный прогон" : $"Часть «{part.Name}»: убрана из регулярного прогона"
            });
        }

        return null;
    }

    private static IReadOnlyList<Guid> PartsOf(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            return ClashScheduleSettings.TryParse(document.RootElement)?.Parts ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    /// <summary>Настройки с новым списком частей; прочие поля — как были.</summary>
    private static string WithParts(string json, IReadOnlyList<Guid> parts)
    {
        JsonObject settings;
        try
        {
            settings = JsonNode.Parse(json) as JsonObject ?? [];
        }
        catch (JsonException)
        {
            settings = [];
        }

        foreach (var key in settings.Select(p => p.Key).Where(k => string.Equals(k, "parts", StringComparison.OrdinalIgnoreCase)).ToList())
            settings.Remove(key);
        settings["parts"] = new JsonArray([.. parts.Select(p => (JsonNode)JsonValue.Create(p))]);
        return settings.ToJsonString(ToadFlowDb.Json);
    }
}
