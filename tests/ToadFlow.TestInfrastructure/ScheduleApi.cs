using System.Text.Json;
using ToadFlow.Contracts;

namespace ToadFlow.Server.Tests.Infrastructure;

public sealed record ScheduleView(
    Guid Id,
    string Name,
    Guid SourceId,
    string OutputFolderTemplate,
    SelectionDto Selection,
    JsonElement ModuleSettings,
    RecurrenceKind RecurrenceKind,
    int DaysOfWeekMask,
    string TimeOfDay,
    int? IntervalHours,
    int Priority,
    int ModelTimeoutMinutes,
    int MaxRetries,
    bool IsEnabled,
    DateTimeOffset? NextRunAt,
    string CreatedBy,
    string UpdatedBy,
    string[] Warnings);

public sealed record RunCreated(Guid RunId);

/// <summary>Расписания для тестов через API — как их создаёт интерфейс.</summary>
public static class ScheduleApi
{
    public static async Task<Guid> SourceAsync(HttpClient admin, SourceKind kind = SourceKind.RevitServer, int? year = 2022)
    {
        var body = kind == SourceKind.RevitServer
            ? new { name = "RS " + Guid.NewGuid().ToString("N")[..4], kind, host = "rs22", revitYear = year, rootPath = "" }
            : new { name = "NAS " + Guid.NewGuid().ToString("N")[..4], kind, host = @"\\nas\projects", revitYear = (int?)null, rootPath = "" };
        var response = await admin.PostJsonAsync("/api/sources", body);
        response.EnsureSuccessStatusCode();
        return (await response.ReadJsonAsync<IdBody>()).Id;
    }

    public static async Task<Guid> DefaultPresetAsync(HttpClient client) =>
        (await client.GetJsonAsync<IdBody[]>("/api/export/presets"))[0].Id;

    public static object Body(Guid sourceId, Guid presetId, string name = "ЖК Север", SelectionDto? selection = null,
        object? overrides = null, int maxRetries = 1, bool isEnabled = true, RecurrenceKind kind = RecurrenceKind.Weekly,
        string timeOfDay = "20:00:00", int? intervalHours = null, DateTimeOffset? onceAt = null, int mask = 0b0011111) => new
    {
        name,
        moduleKind = ModuleKinds.Export,
        sourceId,
        outputFolderTemplate = @"\\nas\exports\{расписание}\{дата}",
        selection = selection ?? new SelectionDto([new ModelSelectionDto("АР.rvt", "RSN://rs22/ЖК/АР.rvt", true, true)], []),
        moduleSettings = new { presetId, overrides = overrides ?? new { } },
        recurrenceKind = kind,
        daysOfWeekMask = mask,
        timeOfDay,
        intervalHours,
        onceAt,
        priority = 0,
        modelTimeoutMinutes = 30,
        maxRetries,
        isEnabled
    };

    public static async Task<ScheduleView> CreateAsync(HttpClient client, object body)
    {
        var response = await client.PostJsonAsync("/api/schedules", body);
        if (!response.IsSuccessStatusCode) throw new InvalidOperationException(await response.Content.ReadAsStringAsync());
        return await response.ReadJsonAsync<ScheduleView>();
    }

    private sealed record IdBody(Guid Id);
}
