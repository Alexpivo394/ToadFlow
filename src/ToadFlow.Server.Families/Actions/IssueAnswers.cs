using Alexpivo394.Revit.Families.Files;
using Alexpivo394.Revit.Families.Files.Scan;
using Microsoft.EntityFrameworkCore;
using ToadFlow.Server.Data;
using ToadFlow.Server.Families.Cards;
using ToadFlow.Server.Families.Data;
using ToadFlow.Server.Families.Read;
using ToadFlow.Server.Families.Search;

namespace ToadFlow.Server.Families.Actions;

/// <summary>Ответы в «Нужно решение», которые не трогают файлы: меняют карточки так, как их поменял бы обход, знай он ответ.</summary>
internal static class IssueAnswers
{
    /// <summary>
    ///     Неясный случай: файл карточки исчез, появился другой. «Та же» — файл становится новой версией карточки (с её историей), «другое» —
    ///     прежняя карточка уходит в архив, файл получает свою. Следующий обход находит файл уже на месте карточки и ничего не меняет.
    /// </summary>
    /// <returns>Почему нельзя; null — сделано (сохраняет вызывающий).</returns>
    public static async Task<string?> AmbiguousAsync(ToadFlowDb db, FamiliesSettingsStore store, FamilyReadJobs reads, TimeProvider time, FamilyIssue issue, bool same,
        CancellationToken ct)
    {
        var gone = issue.CardId is { } goneId ? await db.Set<FamilyCard>().SingleOrDefaultAsync(c => c.Id == goneId, ct) : null;
        if (gone is null || gone.State == CardState.Archived) return "Прежней карточки уже нет — дождитесь обхода";

        var reviewFolder = (await store.GetAsync(ct)).ReviewFolder;
        var now = time.GetUtcNow();
        var name = LibraryPaths.FamilyName(issue.RelativePath);
        var key = name.ToLowerInvariant();
        if (await db.Set<FamilyCard>().AnyAsync(c => c.NameKey == key && c.State != CardState.Archived && c.Id != gone.Id, ct))
            return $"Имя «{name}» занято другим семейством";

        var card = gone;
        FamilyContent? previous = null;
        if (same)
        {
            var current = gone.CurrentVersionId is { } currentId ? await db.Set<FamilyVersion>().AsNoTracking().SingleOrDefaultAsync(v => v.Id == currentId, ct) : null;
            previous = SearchText.ContentOf(current);
        }
        else
        {
            gone.State = CardState.Archived;
            gone.ArchivedAt = now;
            gone.UpdatedAt = now;
            await db.Set<FamilyVersion>().Where(v => v.CardId == gone.Id && v.State == VersionState.InReview && v.Id != gone.CurrentVersionId)
                .ExecuteUpdateAsync(v => v.SetProperty(x => x.State, VersionState.Rejected), ct);
            await db.SaveChangesAsync(ct); // имя освобождается до новой карточки
            card = new FamilyCard { Id = Guid.CreateVersion7(), CreatedAt = now };
            db.Set<FamilyCard>().Add(card);
        }

        var inReview = LibraryPaths.IsInReview(issue.RelativePath, reviewFolder);
        var version = new FamilyVersion
        {
            Id = Guid.CreateVersion7(),
            CardId = card.Id,
            Hash = issue.Hash!,
            CatalogHash = issue.CatalogHash,
            Size = issue.Size ?? 0,
            RevitYear = issue.RevitYear,
            State = inReview ? VersionState.InReview : VersionState.Approved,
            RelativePath = issue.RelativePath,
            ReadState = ReadState.Pending,
            SeenAt = now
        };
        db.Set<FamilyVersion>().Add(version);
        card.CurrentVersionId = version.Id;
        card.RelativePath = issue.RelativePath;
        card.Section = LibraryPaths.Section(issue.RelativePath, reviewFolder);
        card.SetName(name);
        card.State = inReview ? CardState.InReview : CardState.Approved;
        card.UpdatedAt = now;
        card.ArchivedAt = null;
        CardFields.Refresh(card, version, previous);
        await db.SaveChangesAsync(ct);
        await reads.EnqueueAsync([version.Id], ct);
        return null;
    }

    /// <summary>Нечитаемый файл: версия снова ждёт чтения фермой (например, файл поправили или на ферме появился нужный Revit).</summary>
    public static async Task<string?> RetryReadAsync(ToadFlowDb db, FamilyReadJobs reads, FamilyIssue issue, CancellationToken ct)
    {
        var version = await db.Set<FamilyVersion>().Where(v => v.CardId == issue.CardId && v.Hash == issue.Hash).OrderByDescending(v => v.SeenAt).FirstOrDefaultAsync(ct);
        if (version is null) return "Версии этого файла уже нет";
        version.ReadState = ReadState.Pending;
        version.ReadError = null;
        version.ReadJobId = null;
        await db.SaveChangesAsync(ct);
        await reads.EnqueueAsync([version.Id], ct);
        return null;
    }
}
