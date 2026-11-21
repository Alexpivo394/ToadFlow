using Microsoft.EntityFrameworkCore;
using ToadFlow.Server.Auth;
using ToadFlow.Server.Data;
using ToadFlow.Server.Events;

namespace ToadFlow.Server.Families;

internal static class FamiliesEndpoints
{
    public static void MapFamiliesSettings(this RouteGroupBuilder families)
    {
        families.MapGet("/settings", (FamiliesSettingsStore store, CancellationToken ct) => store.GetAsync(ct));
        families.MapPut("/settings", SaveSettingsAsync).RequireAuthorization(Policies.Admin);
    }

    private static async Task<IResult> SaveSettingsAsync(FamiliesSettings request, FamiliesSettingsStore store, ToadFlowDb db, HttpContext http,
        IDomainEvents events, CancellationToken ct)
    {
        var settings = request.Normalized();
        var errors = settings.Validate().ToList();
        string? agentName = null;
        if (settings.LibraryAgentId is { } agentId)
        {
            agentName = await db.Agents.Where(a => a.Id == agentId).Select(a => a.MachineName).SingleOrDefaultAsync(ct);
            if (agentName is null) errors.Add("Агент библиотеки не найден");
        }

        if (errors.Count > 0) return Results.BadRequest(new { error = string.Join("; ", errors) });

        await store.SaveAsync(settings, ct);
        var root = settings.Configured ? $"«{settings.Root}»" : "не задан";
        events.Publish(new SettingsChanged(http.User.Actor(),
            $"Семейства: корень библиотеки {root}, папка проверки «{settings.ReviewFolder}», обход раз в {settings.ScanIntervalMinutes} мин., агент библиотеки — {agentName ?? "любой"}"));
        return Results.Ok(settings);
    }
}
