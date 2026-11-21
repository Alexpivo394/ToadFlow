using System.Security.Claims;
using Alexpivo394.Revit.Families.Files.Scan;
using Microsoft.EntityFrameworkCore;
using ToadFlow.Contracts.Families;
using ToadFlow.Server.Auth;
using ToadFlow.Server.Data;
using ToadFlow.Server.Events;
using ToadFlow.Server.Families.Cards;
using ToadFlow.Server.Families.Data;
using ToadFlow.Server.Families.Read;
using ToadFlow.Server.Families.Scan;
using ToadFlow.Server.Families.Search;

namespace ToadFlow.Server.Families.Actions;

/// <param name="Folder">Папка от корня библиотеки: «ОВ\Клапаны».</param>
public sealed record ApproveRequest(string Folder);

public sealed record RejectVersionRequest(string Reason);

/// <param name="Answer">
///     Дубль: <c>delete</c> — удалить копию, <c>keep</c> — оставить как есть. Конфликт имён: <c>rename</c> (с <paramref name="Name" />),
///     <c>delete</c>. Неясный случай: <c>same</c> — новая версия того семейства, <c>other</c> — другое семейство. Нечитаемый: <c>retry</c>.
/// </param>
/// <param name="Name">Новое имя файла без расширения — для <c>rename</c>.</param>
public sealed record IssueAnswer(string Answer, string? Name);

/// <summary>
///     Кнопки координатора, которые меняют файлы библиотеки (этап 4): утвердить и переложить, отклонить, убрать в архив, вернуть из архива,
///     вернуть прошлую версию, ответить в «Нужно решение». Файлы трогает агент библиотеки; карточки меняет обход, который начинается сразу.
/// </summary>
internal static class ActionEndpoints
{
    private const int MaxReason = 1000;

    public static void MapFamiliesActions(this RouteGroupBuilder families)
    {
        families.MapGet("/actions", (LibraryFiles files, CancellationToken ct) => files.StateAsync(ct));
        families.MapGet("/folders", FoldersAsync);
        families.MapPost("/{id:guid}/approve", ApproveNewAsync).RequireAuthorization(Policies.Operator);
        families.MapPost("/{id:guid}/archive", ArchiveAsync).RequireAuthorization(Policies.Operator);
        families.MapPost("/{id:guid}/restore", RestoreAsync).RequireAuthorization(Policies.Operator);
        families.MapPost("/versions/{id:guid}/approve", ApproveCandidateAsync).RequireAuthorization(Policies.Operator);
        families.MapPost("/versions/{id:guid}/reject", RejectAsync).RequireAuthorization(Policies.Operator);
        families.MapPost("/versions/{id:guid}/revert", RevertAsync).RequireAuthorization(Policies.Operator);
        families.MapPost("/issues/{id:guid}/answer", AnswerAsync).RequireAuthorization(Policies.Operator);
    }

    /// <summary>Папки библиотеки, где уже лежат утверждённые семейства, со всеми родительскими: куда утверждать новое.</summary>
    private static async Task<List<string>> FoldersAsync(ToadFlowDb db, CancellationToken ct)
    {
        var paths = await db.Set<FamilyCard>().AsNoTracking().Where(c => c.State == CardState.Approved).Select(c => c.RelativePath).ToListAsync(ct);
        var folders = new SortedSet<string>(StringComparer.CurrentCultureIgnoreCase);
        foreach (var path in paths)
        {
            var parts = path.Split('\\');
            for (var depth = 1; depth < parts.Length; depth++) folders.Add(string.Join('\\', parts.Take(depth)));
        }

        return folders.ToList();
    }

    private static async Task<IResult> ApproveNewAsync(Guid id, ApproveRequest body, ToadFlowDb db, FamiliesSettingsStore store, LibraryFiles files, ClaimsPrincipal user,
        IDomainEvents events, CancellationToken ct)
    {
        var (card, current) = await CardAsync(db, id, ct);
        if (card is null || current is null) return Results.NotFound();
        if (card.State != CardState.InReview) return Error("Утвердить и переложить можно только новое семейство из папки проверки");
        var settings = await store.GetAsync(ct);
        if (Folder(body.Folder, settings.ReviewFolder) is not { } folder)
            return Error("Укажите папку библиотеки вне папки проверки и служебной папки, без «..»");

        var target = $@"{folder}\{Path.GetFileName(card.RelativePath)}";
        return await DoAsync(files.RunAsync(FamilyFileActionKind.Move, card.RelativePath, target, current.Hash, current.CatalogHash, null, ct), events, user, card,
            $"утверждено и переложено в «{folder}»");
    }

    private static async Task<IResult> ApproveCandidateAsync(Guid id, ToadFlowDb db, LibraryFiles files, ClaimsPrincipal user, IDomainEvents events, CancellationToken ct)
    {
        var (version, card, current) = await VersionAsync(db, id, ct);
        if (version is null || card is null) return Results.NotFound();
        if (card.State != CardState.Approved || version.State != VersionState.InReview || version.Id == card.CurrentVersionId || current is null)
            return Error("Это не версия-кандидат утверждённого семейства");

        return await DoAsync(files.RunAsync(FamilyFileActionKind.Move, version.RelativePath, card.RelativePath, version.Hash, version.CatalogHash, current.Hash, ct),
            events, user, card, "утверждена новая версия");
    }

    private static async Task<IResult> RejectAsync(Guid id, RejectVersionRequest body, ToadFlowDb db, LibraryFiles files, ClaimsPrincipal user, IDomainEvents events,
        CancellationToken ct)
    {
        var reason = body.Reason?.Trim() ?? "";
        if (reason.Length == 0 || reason.Length > MaxReason) return Error("Напишите причину отклонения");
        var (version, card, _) = await VersionAsync(db, id, ct);
        if (version is null || card is null) return Results.NotFound();
        if (version.State != VersionState.InReview || card.State == CardState.Archived) return Error("Отклонить можно только то, что лежит в папке проверки");

        var path = version.Id == card.CurrentVersionId ? card.RelativePath : version.RelativePath;
        var error = await files.RunAsync(FamilyFileActionKind.Delete, path, null, version.Hash, version.CatalogHash, null, ct);
        if (error is null)
        {
            version.Note = $"отклонено: {reason}";
            await db.SaveChangesAsync(ct);
        }

        return await DoAsync(Task.FromResult(error), events, user, card, $"отклонено «{path}»: {reason}");
    }

    private static async Task<IResult> ArchiveAsync(Guid id, ToadFlowDb db, LibraryFiles files, ClaimsPrincipal user, IDomainEvents events, CancellationToken ct)
    {
        var (card, current) = await CardAsync(db, id, ct);
        if (card is null || current is null) return Results.NotFound();
        if (card.State == CardState.Archived) return Error("Семейство уже в архиве");

        return await DoAsync(files.RunAsync(FamilyFileActionKind.Delete, card.RelativePath, null, current.Hash, current.CatalogHash, null, ct), events, user, card,
            "убрано в архив");
    }

    private static async Task<IResult> RestoreAsync(Guid id, ToadFlowDb db, LibraryFiles files, ClaimsPrincipal user, IDomainEvents events, CancellationToken ct)
    {
        var (card, current) = await CardAsync(db, id, ct);
        if (card is null || current is null) return Results.NotFound();
        if (card.State != CardState.Archived) return Error("Семейство не в архиве");
        if (await db.Set<FamilyCard>().AnyAsync(c => c.NameKey == card.NameKey && c.State != CardState.Archived && c.Id != card.Id, ct))
            return Results.Conflict(new { error = $"Имя «{card.Name}» уже занято другим семейством библиотеки" });

        return await DoAsync(files.RunAsync(FamilyFileActionKind.Restore, card.RelativePath, null, current.Hash, current.CatalogHash, null, ct), events, user, card,
            $"возвращено из архива в «{card.RelativePath}»");
    }

    private static async Task<IResult> RevertAsync(Guid id, ToadFlowDb db, LibraryFiles files, TimeProvider time, ClaimsPrincipal user, IDomainEvents events,
        CancellationToken ct)
    {
        var (version, card, current) = await VersionAsync(db, id, ct);
        if (version is null || card is null) return Results.NotFound();
        if (card.State != CardState.Approved || current is null || version.Id == card.CurrentVersionId || version.State != VersionState.Approved)
            return Error("Вернуть можно прошлую утверждённую версию утверждённого семейства");
        if (version.Hash == current.Hash && version.CatalogHash == current.CatalogHash) return Error("Эта версия и так действует");

        // Пометку версии обход возьмёт из публикации: она должна ждать до того, как агент положит файл и обход его увидит.
        var publication = new FamilyPublication
        {
            Id = Guid.CreateVersion7(),
            RelativePath = card.RelativePath,
            Hash = version.Hash,
            AuthorUserId = user.UserId(),
            Note = $"возврат к версии от {version.SeenAt:dd.MM.yyyy}",
            CardId = card.Id,
            CreatedAt = time.GetUtcNow()
        };
        db.Set<FamilyPublication>().Add(publication);
        await db.SaveChangesAsync(ct);

        var error = await files.RunAsync(FamilyFileActionKind.Restore, card.RelativePath, null, version.Hash, version.CatalogHash, current.Hash, ct);
        if (error is not null)
        {
            db.Set<FamilyPublication>().Remove(publication);
            await db.SaveChangesAsync(CancellationToken.None);
        }

        return await DoAsync(Task.FromResult(error), events, user, card, publication.Note);
    }

    private static async Task<IResult> AnswerAsync(Guid id, IssueAnswer body, ToadFlowDb db, LibraryFiles files, FamiliesSettingsStore store, FamilyReadJobs reads,
        TimeProvider time, ClaimsPrincipal user, IDomainEvents events, CancellationToken ct)
    {
        var issue = await db.Set<FamilyIssue>().SingleOrDefaultAsync(i => i.Id == id, ct);
        if (issue is null) return Results.NotFound();
        if (issue.ResolvedAt is not null) return Error("Случай уже решён");
        if (issue.Hash is null) return Error("У случая нет файла");

        string? error;
        string resolution;
        switch (issue.Kind, body.Answer)
        {
            case (IssueKind.Duplicate or IssueKind.NameConflict, "delete"):
                error = await files.RunAsync(FamilyFileActionKind.Delete, issue.RelativePath, null, issue.Hash, issue.CatalogHash, null, ct);
                resolution = "файл удалён";
                break;
            case (IssueKind.Duplicate, "keep"):
                issue.Dismissed = true;
                error = null;
                resolution = "оставлено как есть";
                break;
            case (IssueKind.NameConflict, "rename"):
            {
                var name = body.Name?.Trim() ?? "";
                if (name.Length == 0 || name.Length > 200 || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || name.EndsWith('.'))
                    return Error("Новое имя — без знаков \\ / : * ? \" < > | и не длиннее 200");
                var key = name.ToLowerInvariant();
                if (await db.Set<FamilyCard>().AnyAsync(c => c.NameKey == key && c.State != CardState.Archived, ct)) return Error($"Имя «{name}» тоже занято");
                var folder = Path.GetDirectoryName(issue.RelativePath) ?? "";
                var target = folder.Length == 0 ? $"{name}.rfa" : $@"{folder}\{name}.rfa";
                error = await files.RunAsync(FamilyFileActionKind.Move, issue.RelativePath, target, issue.Hash, issue.CatalogHash, null, ct);
                resolution = $"переименован в «{name}»";
                break;
            }
            case (IssueKind.Ambiguous, "same" or "other"):
                error = await IssueAnswers.AmbiguousAsync(db, store, reads, time, issue, body.Answer == "same", ct);
                resolution = body.Answer == "same" ? "это новая версия того семейства" : "это другое семейство";
                break;
            case (IssueKind.Unreadable, "retry"):
                error = await IssueAnswers.RetryReadAsync(db, reads, issue, ct);
                resolution = "прочитать снова";
                break;
            default:
                return Error("Такого ответа на этот случай нет");
        }

        if (error is not null) return Error(error);
        issue.ResolvedAt = time.GetUtcNow();
        issue.Resolution = resolution;
        await db.SaveChangesAsync(ct);
        events.Publish(new CatalogChanged(user.Actor(), ScanApplier.UiEntity, issue.CardId ?? Guid.Empty,
            $"Семейства: «{issue.RelativePath}» — {resolution}"));
        return Results.Ok();
    }

    private static async Task<IResult> DoAsync(Task<string?> action, IDomainEvents events, ClaimsPrincipal user, FamilyCard card, string what)
    {
        if (await action is { } error) return Error(error);
        events.Publish(new CatalogChanged(user.Actor(), ScanApplier.UiEntity, card.Id, $"Семейства: «{card.Name}» — {what}"));
        return Results.Ok();
    }

    private static IResult Error(string error) => Results.BadRequest(new { error = char.ToUpperInvariant(error[0]) + error[1..] });

    private static async Task<(FamilyCard? Card, FamilyVersion? Current)> CardAsync(ToadFlowDb db, Guid id, CancellationToken ct)
    {
        var card = await db.Set<FamilyCard>().AsNoTracking().SingleOrDefaultAsync(c => c.Id == id, ct);
        var current = card?.CurrentVersionId is { } currentId ? await db.Set<FamilyVersion>().AsNoTracking().SingleOrDefaultAsync(v => v.Id == currentId, ct) : null;
        return (card, current);
    }

    private static async Task<(FamilyVersion? Version, FamilyCard? Card, FamilyVersion? Current)> VersionAsync(ToadFlowDb db, Guid id, CancellationToken ct)
    {
        var version = await db.Set<FamilyVersion>().SingleOrDefaultAsync(v => v.Id == id, ct);
        if (version is null) return (null, null, null);
        var (card, current) = await CardAsync(db, version.CardId, ct);
        return (version, card, current);
    }

    /// <summary>Папка от корня без переходов наверх, вне папки проверки и служебной папки; null — такая не годится.</summary>
    internal static string? Folder(string? folder, string reviewFolder)
    {
        var parts = (folder ?? "").Replace('/', '\\').Trim().Trim('\\').Split('\\', StringSplitOptions.TrimEntries);
        if (parts.Length == 0 || parts.Any(p => p is "" or "." or ".." || p.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)) return null;
        if (parts[0].Equals(reviewFolder, StringComparison.OrdinalIgnoreCase) || parts[0].Equals(LibraryPaths.ServiceFolder, StringComparison.OrdinalIgnoreCase)) return null;
        return string.Join('\\', parts);
    }
}
