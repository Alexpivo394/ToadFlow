using System.Text.Json;
using Alexpivo394.Revit.Clash.Rules;
using Microsoft.EntityFrameworkCore;
using ToadFlow.Server.Auth;
using ToadFlow.Server.Data;
using ToadFlow.Server.Data.Entities;
using ToadFlow.Server.Events;

namespace ToadFlow.Server.Clash.Parts;

/// <summary>
///     Утверждение черновика части: новая версия конфигурации, по которой работают расписания. «Сбросить черновик» берёт
///     последнюю утверждённую версию обратно в черновик. Утверждает и сбрасывает Оператор, версии читает любая роль:
///     история версий видна и на странице проверки.
/// </summary>
public static class ApproveEndpoints
{
    /// <param name="members">Группа участников: маршруты открыты и проектировщику.</param>
    public static void MapClashVersions(this RouteGroupBuilder members)
    {
        var part = members.MapGroup("/clash/parts/{id:guid}");
        part.MapGet("/versions", VersionsAsync);
        part.MapGet("/versions/{version:int}", VersionAsync);
        part.MapGet("/versions/{version:int}/changes", ApprovePreviews.ChangesAsync);
    }

    public static void MapClashApprove(this RouteGroupBuilder api)
    {
        var write = api.MapGroup("/clash/parts/{id:guid}").RequireAuthorization(Policies.Operator);
        write.MapPost("/approve", ApproveAsync);
        write.MapGet("/approve/preview", ApprovePreviews.PreviewAsync);
        write.MapPost("/draft/reset", ResetAsync);
    }

    /// <summary>Одинаковы ли конфигурации: сравниваются в каноническом JSON.</summary>
    public static bool SameConfiguration(string a, string b) =>
        ConfigurationJson.Serialize(ConfigurationJson.Deserialize(a)) == ConfigurationJson.Serialize(ConfigurationJson.Deserialize(b));

    private static async Task<IResult> ApproveAsync(Guid id, ApproveRequest request, ToadFlowDb db, PartModels models, HttpContext http, TimeProvider time,
        IDomainEvents events, Collisions.ApprovedRuns runs, CancellationToken ct)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var part = await db.Set<ClashPart>().FromSqlInterpolated($"SELECT * FROM clash_parts WHERE id = {id} FOR UPDATE").SingleOrDefaultAsync(ct);
        if (part is null) return Results.NotFound();

        var partModels = await models.OfPartAsync(id, ct);
        var errors = DraftRunChecks.Problems(partModels, ConfigurationJson.Deserialize(part.DraftJson));
        if (errors.Count > 0) return Results.BadRequest(new { error = string.Join("; ", errors), errors });
        var composition = VersionModels.Of(partModels);
        if (part.ApprovedVersion is { } approved)
        {
            var current = await db.Set<ClashPartVersion>().AsNoTracking().SingleAsync(v => v.PartId == id && v.Version == approved, ct);
            // Версия без снимка состава считается с нынешним составом: её прогоны берут его.
            var sameModels = VersionModels.Parse(current.ModelsJson) is not { } frozen || VersionModels.Same(frozen, composition);
            if (sameModels && SameConfiguration(current.ConfigurationJson, part.DraftJson))
                return Results.BadRequest(new { error = $"Черновик и модели части совпадают с утверждённой версией {approved}: изменений нет" });
        }

        var version = new ClashPartVersion
        {
            PartId = id,
            Version = (part.ApprovedVersion ?? 0) + 1,
            ConfigurationJson = part.DraftJson,
            ModelsJson = VersionModels.Serialize(composition),
            Comment = string.IsNullOrWhiteSpace(request.Comment) ? null : request.Comment.Trim(),
            UserId = http.User.UserId(),
            ApprovedAt = time.GetUtcNow()
        };
        db.Set<ClashPartVersion>().Add(version);
        part.ApprovedVersion = version.Version;
        if (request.Schedules is { } schedules && await PartSchedules.ApplyAsync(db, part, schedules, version.UserId, version.ApprovedAt, ct) is { } scheduleError)
            return Results.BadRequest(new { error = scheduleError });
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);

        events.Publish(new ClashPartApproved(http.User.Actor(), id, part.Name, version.Version));
        var run = request.Run ? await runs.EnqueueAsync(id, null, [], http.User.Actor(), ct) : null;
        return Results.Ok(new ApprovedVersionView(version.Version, version.ApprovedAt, await UserNameAsync(db, version.UserId, ct), version.Comment, run?.Id));
    }

    private static async Task<IResult> VersionsAsync(Guid id, ToadFlowDb db, CancellationToken ct)
    {
        var versions = await db.Set<ClashPartVersion>().AsNoTracking().Where(v => v.PartId == id).OrderByDescending(v => v.Version)
            .GroupJoin(db.Set<User>().AsNoTracking(), v => v.UserId, u => u.Id, (v, users) => new { v, users })
            .SelectMany(x => x.users.DefaultIfEmpty(), (x, u) => new ApprovedVersionView(x.v.Version, x.v.ApprovedAt, u == null ? "" : u.DisplayName, x.v.Comment, null))
            .ToListAsync(ct);
        return Results.Ok(versions);
    }

    private static async Task<IResult> VersionAsync(Guid id, int version, ToadFlowDb db, CancellationToken ct)
    {
        var stored = await db.Set<ClashPartVersion>().AsNoTracking().SingleOrDefaultAsync(v => v.PartId == id && v.Version == version, ct);
        if (stored is null) return Results.NotFound();
        using var document = JsonDocument.Parse(stored.ConfigurationJson);
        return Results.Ok(new ApprovedVersionDetails(stored.Version, stored.ApprovedAt, await UserNameAsync(db, stored.UserId, ct), stored.Comment,
            document.RootElement.Clone()));
    }

    /// <summary>«Вернуться в черновик»: черновик := последняя утверждённая версия, обычным сохранением с новой версией черновика.</summary>
    private static async Task<IResult> ResetAsync(Guid id, ToadFlowDb db, PartModels models, HttpContext http, TimeProvider time, IDomainEvents events,
        CancellationToken ct)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var part = await db.Set<ClashPart>().FromSqlInterpolated($"SELECT * FROM clash_parts WHERE id = {id} FOR UPDATE").SingleOrDefaultAsync(ct);
        if (part is null) return Results.NotFound();
        if (part.ApprovedVersion is not { } approved) return Results.BadRequest(new { error = "Часть ещё не утверждалась" });

        var json = await db.Set<ClashPartVersion>().Where(v => v.PartId == id && v.Version == approved).Select(v => v.ConfigurationJson).SingleAsync(ct);
        await DraftEndpoints.StoreAsync(db, part, ConfigurationJson.Deserialize(json), http, time, ct);
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        events.Publish(new Engine.ClashDraftChanged(http.User.Actor(), id));
        return Results.Ok(await DraftEndpoints.ViewAsync(part, models, ct));
    }

    private static async Task<string> UserNameAsync(ToadFlowDb db, Guid userId, CancellationToken ct) =>
        await db.Set<User>().Where(u => u.Id == userId).Select(u => u.DisplayName).SingleOrDefaultAsync(ct) ?? "";
}
