using Microsoft.EntityFrameworkCore;
using ToadFlow.Contracts;
using ToadFlow.Contracts.Export;
using ToadFlow.Server.Auth;
using ToadFlow.Server.Data;
using ToadFlow.Server.Events;

namespace ToadFlow.Server.Export;

public sealed record PresetRequest(string Name, bool IsDefault, ExportSettingsDto Settings);

internal static class PresetEndpoints
{
    public static void MapPresets(this RouteGroupBuilder api)
    {
        var presets = api.MapGroup("/export/presets");
        presets.MapGet("/", async (ToadFlowDb db) =>
            await db.Set<ExportPreset>().AsNoTracking().OrderByDescending(p => p.IsDefault).ThenBy(p => p.Name).ToListAsync());

        var write = presets.MapGroup("").RequireAuthorization(Policies.Operator);
        write.MapPost("/", async (PresetRequest request, ToadFlowDb db, HttpContext http, IDomainEvents events) =>
        {
            if (Validate(request) is { } error) return Results.BadRequest(new { error });
            if (await NameTakenAsync(db, request.Name, null)) return Results.Conflict(new { error = $"Пресет «{request.Name.Trim()}» уже есть" });

            var preset = new ExportPreset { Id = Guid.CreateVersion7() };
            await ApplyAsync(db, preset, request);
            db.Set<ExportPreset>().Add(preset);
            await db.SaveChangesAsync();
            events.Publish(new CatalogChanged(http.User.Actor(), "presets", preset.Id, $"Добавлен пресет выгрузки «{preset.Name}»"));
            return Results.Created($"/api/export/presets/{preset.Id}", preset);
        });

        write.MapPut("/{id:guid}", async (Guid id, PresetRequest request, ToadFlowDb db, HttpContext http, IDomainEvents events) =>
        {
            if (Validate(request) is { } error) return Results.BadRequest(new { error });
            var preset = await db.Set<ExportPreset>().FindAsync(id);
            if (preset is null) return Results.NotFound();
            if (await NameTakenAsync(db, request.Name, id)) return Results.Conflict(new { error = $"Пресет «{request.Name.Trim()}» уже есть" });
            if (preset.IsDefault && !request.IsDefault)
                return Results.Conflict(new { error = "Пресет по умолчанию нельзя снять — назначьте по умолчанию другой" });

            await ApplyAsync(db, preset, request);
            await db.SaveChangesAsync();
            events.Publish(new CatalogChanged(http.User.Actor(), "presets", id, $"Изменён пресет выгрузки «{preset.Name}»"));
            return Results.Ok(preset);
        });

        write.MapDelete("/{id:guid}", async (Guid id, ToadFlowDb db, HttpContext http, IDomainEvents events) =>
        {
            var preset = await db.Set<ExportPreset>().FindAsync(id);
            if (preset is null) return Results.NotFound();
            if (preset.IsDefault) return Results.Conflict(new { error = "Пресет по умолчанию удалить нельзя" });
            if (await UsedBySchedulesAsync(db, id))
                return Results.Conflict(new { error = "Пресет используется в расписаниях — сначала поменяйте его там" });

            db.Set<ExportPreset>().Remove(preset);
            await db.SaveChangesAsync();
            events.Publish(new CatalogChanged(http.User.Actor(), "presets", id, $"Удалён пресет выгрузки «{preset.Name}»"));
            return Results.NoContent();
        });
    }

    private static async Task ApplyAsync(ToadFlowDb db, ExportPreset preset, PresetRequest request)
    {
        preset.Name = request.Name.Trim();
        preset.Settings = request.Settings;
        if (request.IsDefault && !preset.IsDefault)
        {
            await db.Set<ExportPreset>().Where(p => p.IsDefault).ExecuteUpdateAsync(p => p.SetProperty(x => x.IsDefault, false));
            preset.IsDefault = true;
        }
    }

    private static Task<bool> NameTakenAsync(ToadFlowDb db, string name, Guid? exceptId)
    {
        var trimmed = name.Trim();
        return db.Set<ExportPreset>().AnyAsync(p => p.Name == trimmed && p.Id != exceptId);
    }

    private static async Task<bool> UsedBySchedulesAsync(ToadFlowDb db, Guid presetId)
    {
        var settings = await db.Schedules.Where(s => s.ModuleKind == ModuleKinds.Export).Select(s => s.ModuleSettingsJson).ToListAsync();
        return settings.Any(json => ExportScheduleSettings.TryParse(json)?.PresetId == presetId);
    }

    private static string? Validate(PresetRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Name)) return "Укажите название пресета";
        if (request.Settings is null) return "Нет настроек";
        if (!(request.Settings.NwcFacetingFactor > 0)) return "Коэффициент фасетизации NWC должен быть больше нуля";
        return null;
    }
}
