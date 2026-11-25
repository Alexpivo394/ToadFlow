using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using ToadFlow.Contracts.Families;
using ToadFlow.Server.Auth;
using ToadFlow.Server.Data;
using ToadFlow.Server.Events;
using ToadFlow.Server.Families.Actions;
using ToadFlow.Server.Families.Data;
using ToadFlow.Server.Families.Scan;

namespace ToadFlow.Server.Families.Cards;

/// <summary>Флаги семейства: «только координаторы» и «без оповещений».</summary>
public sealed record FamilyFlagsRequest(bool CoordinatorsOnly, bool Silent);

/// <param name="Name">Название итерации — имя её папки.</param>
/// <param name="FromVersionId">От какой версии (любой версии карточки, любой итерации).</param>
public sealed record NewIterationRequest(string Name, Guid FromVersionId, string? Comment, bool CoordinatorsOnly);

public sealed record IterationUpdateRequest(string? Comment, bool CoordinatorsOnly);

/// <param name="VersionId">Версия итерации, которая становится финальной; null — действующая.</param>
/// <param name="Comment">Что проделано — обязательно.</param>
public sealed record IterationFinalRequest(Guid? VersionId, string Comment);

/// <summary>
///     Итерации семейства и флаги карточки — координатор. Новая итерация из веба: агент библиотеки кладёт содержимое выбранной версии из
///     <c>.toadflow\history</c> в папку итерации; версию итерации создаст обход, увидев файл.
/// </summary>
internal static class IterationEndpoints
{
    private const int MaxText = 2000;

    public static void MapFamiliesIterations(this RouteGroupBuilder families)
    {
        families.MapPut("/{id:guid}/flags", FlagsAsync).RequireAuthorization(Policies.Operator);
        families.MapPost("/{id:guid}/iterations", CreateAsync).RequireAuthorization(Policies.Operator);
        families.MapPut("/iterations/{id:guid}", UpdateAsync).RequireAuthorization(Policies.Operator);
        families.MapPost("/iterations/{id:guid}/final", FinalAsync).RequireAuthorization(Policies.Operator);
        families.MapDelete("/iterations/{id:guid}/final", UnfinalAsync).RequireAuthorization(Policies.Operator);
        families.MapPost("/iterations/{id:guid}/archive", ArchiveAsync).RequireAuthorization(Policies.Operator);
        families.MapPost("/iterations/{id:guid}/restore", RestoreAsync).RequireAuthorization(Policies.Operator);
    }

    private static async Task<IResult> FlagsAsync(Guid id, FamilyFlagsRequest request, ToadFlowDb db, TimeProvider time, ClaimsPrincipal user, IDomainEvents events,
        CancellationToken ct)
    {
        var card = await db.Set<FamilyCard>().SingleOrDefaultAsync(c => c.Id == id, ct);
        if (card is null) return Results.NotFound();
        card.CoordinatorsOnly = request.CoordinatorsOnly;
        card.Silent = request.Silent;
        card.UpdatedAt = time.GetUtcNow();
        await db.SaveChangesAsync(ct);
        Publish(events, user, card.Id, $"Семейства: «{card.Name}» — {(card.CoordinatorsOnly ? "только координаторы" : "загружают все")}, " +
                                       $"{(card.Silent ? "без оповещений" : "с оповещениями")}");
        return Results.Ok();
    }

    private static async Task<IResult> CreateAsync(Guid id, NewIterationRequest request, ToadFlowDb db, FamiliesSettingsStore store, LibraryFiles files,
        TimeProvider time, ClaimsPrincipal user, IDomainEvents events, CancellationToken ct)
    {
        var card = await db.Set<FamilyCard>().AsNoTracking().SingleOrDefaultAsync(c => c.Id == id, ct);
        if (card is null) return Results.NotFound();
        if (card.State == CardState.Archived) return Error("Семейство в архиве — сначала верните его");
        if (IterationPaths.NameError(request.Name) is { } nameError) return Error(nameError);
        if (request.Comment?.Length > MaxText) return Error($"Комментарий — до {MaxText} знаков");
        var name = request.Name.Trim();
        var key = name.ToLowerInvariant();
        if (await db.Set<FamilyIteration>().AnyAsync(i => i.CardId == id && i.NameKey == key, ct))
            return Results.Conflict(new { error = $"Итерация «{name}» у этого семейства уже есть" });
        var source = await db.Set<FamilyVersion>().AsNoTracking().SingleOrDefaultAsync(v => v.Id == request.FromVersionId && v.CardId == id, ct);
        if (source is null) return Error("Выберите версию этого семейства, от которой делается итерация");

        var settings = await store.GetAsync(ct);
        var now = time.GetUtcNow();
        var path = IterationPaths.PathOf(settings.IterationsFolder, card.Name, name);
        var iteration = new FamilyIteration
        {
            Id = Guid.CreateVersion7(), CardId = id, Comment = Clean(request.Comment), RelativePath = path, CoordinatorsOnly = request.CoordinatorsOnly,
            CreatedAt = now, UpdatedAt = now
        };
        iteration.SetName(name);
        var publication = new FamilyPublication
        {
            Id = Guid.CreateVersion7(), RelativePath = path, Hash = source.Hash, AuthorUserId = user.UserId(), CardId = id, IterationId = iteration.Id,
            Note = $"итерация от версии от {source.SeenAt:dd.MM.yyyy}", CreatedAt = now
        };
        db.Set<FamilyIteration>().Add(iteration);
        db.Set<FamilyPublication>().Add(publication);
        await db.SaveChangesAsync(ct);

        var error = await files.RunAsync(FamilyFileActionKind.Restore, path, null, source.Hash, source.CatalogHash, null, ct);
        if (error is not null)
        {
            db.Set<FamilyPublication>().Remove(publication);
            db.Set<FamilyIteration>().Remove(iteration);
            await db.SaveChangesAsync(CancellationToken.None);
            return Error(error);
        }

        Publish(events, user, id, $"Семейства: «{card.Name}» — новая итерация «{name}»");
        return Results.Ok(new { id = iteration.Id });
    }

    private static async Task<IResult> UpdateAsync(Guid id, IterationUpdateRequest request, ToadFlowDb db, TimeProvider time, ClaimsPrincipal user,
        IDomainEvents events, CancellationToken ct)
    {
        if (request.Comment?.Length > MaxText) return Error($"Комментарий — до {MaxText} знаков");
        var iteration = await db.Set<FamilyIteration>().SingleOrDefaultAsync(i => i.Id == id, ct);
        if (iteration is null) return Results.NotFound();
        iteration.Comment = Clean(request.Comment);
        iteration.CoordinatorsOnly = request.CoordinatorsOnly;
        iteration.UpdatedAt = time.GetUtcNow();
        await db.SaveChangesAsync(ct);
        Publish(events, user, iteration.CardId, $"Семейства: изменена итерация «{iteration.Name}»");
        return Results.Ok();
    }

    private static async Task<IResult> FinalAsync(Guid id, IterationFinalRequest request, ToadFlowDb db, TimeProvider time, ClaimsPrincipal user,
        IDomainEvents events, CancellationToken ct)
    {
        var comment = request.Comment?.Trim() ?? "";
        if (comment.Length == 0) return Error("Напишите, что проделано в финальной версии");
        if (comment.Length > MaxText) return Error($"Комментарий — до {MaxText} знаков");
        var iteration = await db.Set<FamilyIteration>().SingleOrDefaultAsync(i => i.Id == id, ct);
        if (iteration is null) return Results.NotFound();
        var versionId = request.VersionId ?? iteration.CurrentVersionId;
        if (versionId is null || !await db.Set<FamilyVersion>().AnyAsync(v => v.Id == versionId && v.IterationId == id, ct))
            return Error("Финальной можно сделать только версию этой итерации");

        iteration.FinalVersionId = versionId;
        iteration.FinalComment = comment;
        iteration.FinalByUserId = user.UserId();
        iteration.FinalAt = time.GetUtcNow();
        iteration.UpdatedAt = iteration.FinalAt.Value;
        await db.SaveChangesAsync(ct);
        Publish(events, user, iteration.CardId, $"Семейства: итерация «{iteration.Name}» завершена — {comment}");
        return Results.Ok();
    }

    private static async Task<IResult> UnfinalAsync(Guid id, ToadFlowDb db, TimeProvider time, ClaimsPrincipal user, IDomainEvents events, CancellationToken ct)
    {
        var iteration = await db.Set<FamilyIteration>().SingleOrDefaultAsync(i => i.Id == id, ct);
        if (iteration is null) return Results.NotFound();
        iteration.FinalVersionId = null;
        iteration.FinalComment = null;
        iteration.FinalByUserId = null;
        iteration.FinalAt = null;
        iteration.UpdatedAt = time.GetUtcNow();
        await db.SaveChangesAsync(ct);
        Publish(events, user, iteration.CardId, $"Семейства: с итерации «{iteration.Name}» снят финал");
        return Results.Ok();
    }

    private static async Task<IResult> ArchiveAsync(Guid id, ToadFlowDb db, LibraryFiles files, ClaimsPrincipal user, IDomainEvents events, CancellationToken ct)
    {
        var (iteration, current) = await IterationAsync(db, id, ct);
        if (iteration is null) return Results.NotFound();
        if (iteration.RemovedAt is not null || current is null) return Error("Итерация уже убрана или её файл ещё не увиден обходом");
        if (await files.RunAsync(FamilyFileActionKind.Delete, iteration.RelativePath, null, current.Hash, current.CatalogHash, null, ct) is { } error) return Error(error);
        Publish(events, user, iteration.CardId, $"Семейства: итерация «{iteration.Name}» убрана в архив");
        return Results.Ok();
    }

    private static async Task<IResult> RestoreAsync(Guid id, ToadFlowDb db, LibraryFiles files, ClaimsPrincipal user, IDomainEvents events, CancellationToken ct)
    {
        var (iteration, current) = await IterationAsync(db, id, ct);
        if (iteration is null) return Results.NotFound();
        if (iteration.RemovedAt is null || current is null) return Error("Итерация не убрана");
        if (await files.RunAsync(FamilyFileActionKind.Restore, iteration.RelativePath, null, current.Hash, current.CatalogHash, null, ct) is { } error) return Error(error);
        Publish(events, user, iteration.CardId, $"Семейства: итерация «{iteration.Name}» возвращена");
        return Results.Ok();
    }

    private static async Task<(FamilyIteration? Iteration, FamilyVersion? Current)> IterationAsync(ToadFlowDb db, Guid id, CancellationToken ct)
    {
        var iteration = await db.Set<FamilyIteration>().AsNoTracking().SingleOrDefaultAsync(i => i.Id == id, ct);
        var current = iteration?.CurrentVersionId is { } currentId ? await db.Set<FamilyVersion>().AsNoTracking().SingleOrDefaultAsync(v => v.Id == currentId, ct) : null;
        return (iteration, current);
    }

    private static void Publish(IDomainEvents events, ClaimsPrincipal user, Guid cardId, string text) =>
        events.Publish(new CatalogChanged(user.Actor(), ScanApplier.UiEntity, cardId, text));

    private static IResult Error(string error) => Results.BadRequest(new { error });

    private static string? Clean(string? text) => string.IsNullOrWhiteSpace(text) ? null : text.Trim();
}
