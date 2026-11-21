using Alexpivo394.Revit.Families.Files;
using Alexpivo394.Revit.Families.Files.Scan;
using Microsoft.EntityFrameworkCore;
using ToadFlow.Contracts.Families;
using ToadFlow.Server.Data;
using ToadFlow.Server.Events;
using ToadFlow.Server.Families.Cards;
using ToadFlow.Server.Families.Data;
using ToadFlow.Server.Families.Search;

namespace ToadFlow.Server.Families.Scan;

/// <summary>Что изменил один отчёт обхода; <see cref="VersionsToRead" /> — новое содержимое, которое должна прочитать ферма.</summary>
public sealed record ScanApplied(int NewCards, int NewVersions, int Moved, int Archived, int NewIssues, IReadOnlyList<Guid> VersionsToRead)
{
    /// <summary>Публикации, которые этот обход привязал к версиям.</summary>
    public IReadOnlyList<FamilyPublication> Published { get; init; } = [];

    public bool Changed => NewCards + NewVersions + Moved + Archived + NewIssues > 0;
}

/// <summary>
///     Применяет отчёт обхода: сравнивает его с карточками правилами ядра (<see cref="ScanDiff" />) и переносит события в карточки,
///     версии и случаи «Нужно решение» — всё в одной транзакции. Отчёт применяется один раз: обход помечается завершённым в той же
///     транзакции, и второй такой же отчёт (или отчёт обхода, который сервер уже счёл потерянным) ничего не меняет.
/// </summary>
public sealed class ScanApplier(ToadFlowDb db, FamiliesSettingsStore settingsStore, TimeProvider time, IDomainEvents events, ILogger<ScanApplier> logger,
    Requests.RequestNotices notices)
{
    public const string CatalogNote = "изменён каталог типоразмеров";
    public const string UnreviewedNote = "изменено без проверки";
    public const string UiEntity = "families";

    /// <summary>Случаи, которые закрываются сами, когда их причина пропала из отчёта.</summary>
    private static readonly IssueKind[] FileIssues = [IssueKind.NameConflict, IssueKind.Duplicate, IssueKind.Ambiguous];

    private readonly Dictionary<Guid, FamilyCard> _cards = [];
    private readonly Dictionary<Guid, FamilyVersion> _versions = [];
    private readonly List<FamilyIssue> _openIssues = [];
    private readonly HashSet<(IssueKind, string)> _seenIssues = [];

    /// <summary>Случаи, на которые координатор ответил «оставить как есть»: вид, хеш, путь в нижнем регистре.</summary>
    private readonly HashSet<(IssueKind, string, string)> _dismissed = [];
    private readonly List<Guid> _toRead = [];

    /// <summary>Версии, которые этот обход создал или сделал действующими: к ним привязываются публикации.</summary>
    private readonly List<FamilyVersion> _touched = [];
    private string _reviewFolder = FamiliesSettings.DefaultReviewFolder;
    private DateTimeOffset _now;
    private int _newCards, _newVersions, _moved, _archived, _newIssues;

    /// <returns>null — обход неизвестен, уже завершён или запрошен у другого агента: отчёт не применяется.</returns>
    public async Task<ScanApplied?> ApplyAsync(Guid scanId, Guid agentId, FamilyScanReportDto report, CancellationToken ct)
    {
        _now = time.GetUtcNow();
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var errors = report.Errors.ToArray();
        // Отметка «завершён» — первой: второй такой же отчёт ждёт блокировку строки и потом не находит незавершённый обход.
        var claimed = await db.Set<FamilyScan>().Where(s => s.Id == scanId && s.FinishedAt == null && s.AgentId == agentId)
            .ExecuteUpdateAsync(s => s
                .SetProperty(x => x.FinishedAt, _now)
                .SetProperty(x => x.Complete, report.Complete)
                .SetProperty(x => x.Files, report.Files.Count)
                .SetProperty(x => x.Errors, errors), ct);
        if (claimed == 0) return null;

        _reviewFolder = (await settingsStore.GetAsync(ct)).ReviewFolder;
        var scanned = Scanned(report);
        var unsettled = report.Unsettled.Select(Normalize).ToList();
        var certain = report.Complete && unsettled.Count == 0;
        var known = await LoadAsync(ct);
        var questions = _openIssues.Where(i => i is { Kind: IssueKind.Ambiguous, CardId: not null })
            .Select(i => new OpenQuestion(i.CardId!.Value, i.RelativePath)).ToList();
        var diff = ScanDiff.Compare(known, scanned, new ScanOptions(_reviewFolder, report.Complete) { Unsettled = unsettled, OpenQuestions = questions });

        // Сначала архив и отклонённые кандидаты: они освобождают имена.
        foreach (var change in diff)
        {
            if (change is Removed removed) Archive(removed.CardId);
            else if (change is CandidateGone gone) Reject(gone);
        }

        await db.SaveChangesAsync(ct);

        // Смена имён — в две фазы: цепочка «A берёт старое имя B, B уходит под новое» иначе нарушила бы уникальный индекс посередине.
        var renamed = diff.Where(e => e is Moved or NewVersion).Select(e => (CardId: e is Moved m ? m.CardId : ((NewVersion)e).CardId, e.File!))
            .Where(e => !LibraryPaths.FamilyName(e.Item2.RelativePath).Equals(_cards[e.CardId].Name, StringComparison.OrdinalIgnoreCase))
            .ToList();
        foreach (var (cardId, _) in renamed) _cards[cardId].SetName($"\u0001{cardId:N}");
        if (renamed.Count > 0) await db.SaveChangesAsync(ct);

        foreach (var change in diff)
        {
            switch (change)
            {
                case NewFamily family:
                    await AddFamilyAsync(family, ct);
                    break;
                case NewVersion version:
                    NewContent(version);
                    break;
                case Moved moved:
                    Move(moved);
                    break;
                case Candidate candidate:
                    AddCandidate(candidate);
                    break;
                case NameConflict conflict:
                    Issue(IssueKind.NameConflict, conflict.ExistingCardId, conflict.File!, ConflictText(conflict));
                    break;
                case Duplicate duplicate:
                    Issue(IssueKind.Duplicate, duplicate.ExistingCardId, duplicate.File!, DuplicateText(duplicate));
                    break;
                case Ambiguous ambiguous:
                    Issue(IssueKind.Ambiguous, ambiguous.GoneCardId, ambiguous.File!, AmbiguousText(ambiguous));
                    break;
            }
        }

        if (certain)
        {
            foreach (var issue in _openIssues.Where(i => FileIssues.Contains(i.Kind) && !_seenIssues.Contains((i.Kind, i.RelativePath.ToLowerInvariant()))))
                issue.ResolvedAt = _now;
        }

        var published = await PublishAsync(ct);
        // Заявки закрываются в той же транзакции: кто увидел новую карточку, видит и готовую заявку.
        var closed = await Requests.Requests.CloseByPublicationsAsync(db, published, _cards, _versions, _now, ct);
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);

        var applied = new ScanApplied(_newCards, _newVersions, _moved, _archived, _newIssues, _toRead) { Published = published };
        if (applied.Changed)
            events.Publish(new CatalogChanged(Actor.System, UiEntity, scanId,
                $"Семейства: обход библиотеки — новых семейств {_newCards}, новых версий {_newVersions}, перенесено {_moved}, в архив {_archived}, новых случаев {_newIssues}"));
        foreach (var (id, text) in closed) events.Publish(new CatalogChanged(Actor.System, Requests.Requests.UiEntity, id, text));
        foreach (var request in await db.Set<Requests.FamilyRequest>().AsNoTracking().Where(r => closed.Select(c => c.Id).Contains(r.Id)).ToListAsync(ct))
            await notices.MembersAsync(request, Notifications.NotificationKinds.RequestReview,
                $"«{request.Title}»: опубликовано семейство — проверьте, подходит ли", request.AssigneeUserId, ct);
        return applied;
    }

    /// <summary>
    ///     Публикации и возвраты, чей файл этот обход увидел новой версией: версия получает автора, описание и пометку. Публикация ждёт
    ///     не дольше <see cref="PublicationLifetime" />: файл, который так и не появился, её больше не ищет.
    /// </summary>
    private async Task<List<FamilyPublication>> PublishAsync(CancellationToken ct)
    {
        if (_touched.Count == 0) return [];
        var hashes = _touched.Select(v => v.Hash).Distinct().ToList();
        var since = _now - PublicationLifetime;
        var waiting = await db.Set<FamilyPublication>().Where(p => p.AppliedAt == null && p.CreatedAt > since && hashes.Contains(p.Hash))
            .OrderByDescending(p => p.CreatedAt).ToListAsync(ct);
        var published = new List<FamilyPublication>();
        foreach (var version in _touched)
        {
            // Тот же путь — точнее; иначе любая ожидающая с этим хешем (файл переложили до обхода).
            var match = waiting.FirstOrDefault(p => p.VersionId is null && p.Hash == version.Hash && p.RelativePath.Equals(version.RelativePath, StringComparison.OrdinalIgnoreCase))
                        ?? waiting.FirstOrDefault(p => p.VersionId is null && p.Hash == version.Hash);
            if (match is null) continue;

            match.VersionId = version.Id;
            match.AppliedAt = _now;
            version.AuthorUserId = match.AuthorUserId ?? version.AuthorUserId;
            version.Comment = match.Comment ?? version.Comment;
            // Возврат версии кнопкой веба (у него своя пометка) — не «правка мимо проверки». Публикация поверх утверждённого файла остаётся ею:
            // описание и автор у версии есть, но посмотреть её всё равно нужно.
            if (match.Note.Length > 0)
            {
                version.Note = match.Note;
                foreach (var issue in _openIssues.Where(i => i.Kind == IssueKind.UnreviewedChange && i.ResolvedAt == null && i.Hash == version.Hash && i.CardId == version.CardId))
                    issue.ResolvedAt = _now;
            }

            published.Add(match);
        }

        return published;
    }

    /// <summary>Сколько публикация ждёт свой файл в обходах.</summary>
    public static readonly TimeSpan PublicationLifetime = TimeSpan.FromDays(7);

    private List<ScannedFile> Scanned(FamilyScanReportDto report)
    {
        var files = new List<ScannedFile>(report.Files.Count);
        foreach (var file in report.Files)
        {
            if (!IsHash(file.Hash) || file.CatalogHash is { } catalog && !IsHash(catalog))
            {
                logger.LogWarning("Отчёт обхода: у файла {Path} нечитаемый хеш — пропущен", file.RelativePath);
                continue;
            }

            files.Add(new ScannedFile(Normalize(file.RelativePath), file.Size, file.ModifiedUtc, file.Hash, file.CatalogHash, file.RevitYear));
        }

        return files;
    }

    private async Task<List<KnownFile>> LoadAsync(CancellationToken ct)
    {
        var cards = await db.Set<FamilyCard>().Where(c => c.State != CardState.Archived).ToListAsync(ct);
        foreach (var card in cards) _cards[card.Id] = card;
        var versions = await db.Set<FamilyVersion>()
            .Where(v => db.Set<FamilyCard>().Any(c => c.Id == v.CardId && c.State != CardState.Archived && (c.CurrentVersionId == v.Id || v.State == VersionState.InReview)))
            .ToListAsync(ct);
        foreach (var version in versions) _versions[version.Id] = version;
        _openIssues.AddRange(await db.Set<FamilyIssue>().Where(i => i.ResolvedAt == null).ToListAsync(ct));
        foreach (var dismissed in await db.Set<FamilyIssue>().Where(i => i.Dismissed && i.Hash != null).Select(i => new { i.Kind, i.Hash, i.RelativePath }).ToListAsync(ct))
            _dismissed.Add((dismissed.Kind, dismissed.Hash!, dismissed.RelativePath.ToLowerInvariant()));

        var known = new List<KnownFile>();
        foreach (var card in cards)
        {
            if (card.CurrentVersionId is { } currentId && _versions.TryGetValue(currentId, out var current))
                known.Add(new KnownFile(card.Id, card.RelativePath, current.Hash, current.CatalogHash, false));
            if (card.State != CardState.Approved) continue;
            known.AddRange(Candidates(card).Select(v => new KnownFile(card.Id, v.RelativePath, v.Hash, v.CatalogHash, true)));
        }

        return known;
    }

    private IEnumerable<FamilyVersion> Candidates(FamilyCard card) =>
        _versions.Values.Where(v => v.CardId == card.Id && v.Id != card.CurrentVersionId && v.State == VersionState.InReview);

    private void Archive(Guid cardId)
    {
        var card = _cards[cardId];
        card.State = CardState.Archived;
        card.ArchivedAt = _now;
        card.UpdatedAt = _now;
        // Кандидаты архивной карточки становятся обычными файлами папки проверки, а из архива она возвращается только своими версиями.
        foreach (var candidate in Candidates(card).ToList()) candidate.State = VersionState.Rejected;
        _archived++;
    }

    private void Reject(CandidateGone gone)
    {
        if (!_cards.TryGetValue(gone.CardId, out var card)) return;
        foreach (var candidate in Candidates(card).Where(v => v.RelativePath.Equals(gone.RelativePath, StringComparison.OrdinalIgnoreCase)).ToList())
            candidate.State = VersionState.Rejected;
    }

    /// <summary>Новое семейство — или файл архивной карточки, вернувшийся в библиотеку: тогда карточка возвращается из архива.</summary>
    private async Task AddFamilyAsync(NewFamily family, CancellationToken ct)
    {
        var file = family.File!;
        var name = LibraryPaths.FamilyName(file.RelativePath);
        var key = name.ToLowerInvariant();
        var returned = await db.Set<FamilyCard>()
            .Where(c => c.State == CardState.Archived && c.NameKey == key
                        && db.Set<FamilyVersion>().Any(v => v.CardId == c.Id && v.Hash == file.Hash && v.State != VersionState.Rejected))
            .OrderByDescending(c => c.ArchivedAt).FirstOrDefaultAsync(ct);
        if (returned is not null)
        {
            var version = await db.Set<FamilyVersion>().Where(v => v.CardId == returned.Id && v.Hash == file.Hash && v.State != VersionState.Rejected)
                .OrderByDescending(v => v.SeenAt).FirstAsync(ct);
            var previous = returned.CurrentVersionId is { } currentId ? await db.Set<FamilyVersion>().FindAsync([currentId], ct) : null;
            _cards[returned.Id] = returned;
            _versions[version.Id] = version;
            returned.ArchivedAt = null;
            if (version.CatalogHash != file.CatalogHash) version = AddVersion(returned, file, CatalogNote);
            else
            {
                version.State = StateOf(file);
                returned.CurrentVersionId = version.Id;
                _touched.Add(version);
            }

            Place(returned, file);
            CardFields.Refresh(returned, version, SearchText.ContentOf(previous));
            _newCards++;
            return;
        }

        var card = new FamilyCard { Id = Guid.CreateVersion7(), CreatedAt = _now };
        db.Set<FamilyCard>().Add(card);
        _cards[card.Id] = card;
        var added = AddVersion(card, file, "");
        Place(card, file);
        CardFields.Refresh(card, added);
        _newCards++;
    }

    /// <summary>Новое содержимое файла карточки; файл с хешем её кандидата — утверждённый кандидат.</summary>
    private void NewContent(NewVersion change)
    {
        var card = _cards[change.CardId];
        var file = change.File!;
        var previous = card.CurrentVersionId is { } currentId ? _versions.GetValueOrDefault(currentId) : null;
        FamilyVersion current;
        if (Candidates(card).FirstOrDefault(v => v.Hash == file.Hash) is { } approved)
        {
            approved.State = StateOf(file);
            approved.RelativePath = file.RelativePath;
            if (approved.CatalogHash != file.CatalogHash)
            {
                approved.CatalogHash = file.CatalogHash;
                approved.ReadState = ReadState.Pending;
                approved.ContentJson = null;
                _toRead.Add(approved.Id);
            }

            card.CurrentVersionId = approved.Id;
            _touched.Add(approved);
            current = approved;
        }
        else
        {
            var unreviewed = !change.CatalogOnly && !LibraryPaths.IsInReview(file.RelativePath, _reviewFolder);
            current = AddVersion(card, file, change.CatalogOnly ? CatalogNote : unreviewed ? UnreviewedNote : "");
            if (unreviewed)
                Issue(IssueKind.UnreviewedChange, card.Id, file,
                    $"Файл «{file.RelativePath}» перезаписан на месте, мимо папки проверки. Новая версия уже действует у проектировщиков — посмотрите её.");
        }

        Place(card, file);
        CardFields.Refresh(card, current, SearchText.ContentOf(previous));
    }

    private void Move(Moved change)
    {
        var card = _cards[change.CardId];
        var file = change.File!;
        var current = card.CurrentVersionId is { } currentId ? _versions.GetValueOrDefault(currentId) : null;
        if (change.CatalogChanged)
        {
            var previous = current;
            current = AddVersion(card, file, CatalogNote);
            Place(card, file);
            CardFields.Refresh(card, current, SearchText.ContentOf(previous));
        }
        else
        {
            if (current is not null) current.State = StateOf(file);
            Place(card, file);
            CardFields.Refresh(card, current);
        }

        _moved++;
    }

    private void AddCandidate(Candidate change)
    {
        var card = _cards[change.CardId];
        var file = change.File!;
        // Тот же кандидат переложили внутри папки проверки — та же версия, новый путь.
        if (Candidates(card).FirstOrDefault(v => v.Hash == file.Hash) is { } existing)
        {
            existing.RelativePath = file.RelativePath;
            existing.CatalogHash = file.CatalogHash;
            return;
        }

        var version = NewVersionRow(card, file, "", VersionState.InReview);
        db.Set<FamilyVersion>().Add(version);
        _versions[version.Id] = version;
        _toRead.Add(version.Id);
        _touched.Add(version);
        _newVersions++;
    }

    /// <summary>Новая действующая версия карточки.</summary>
    private FamilyVersion AddVersion(FamilyCard card, ScannedFile file, string note)
    {
        var version = NewVersionRow(card, file, note, StateOf(file));
        db.Set<FamilyVersion>().Add(version);
        _versions[version.Id] = version;
        card.CurrentVersionId = version.Id;
        _toRead.Add(version.Id);
        _touched.Add(version);
        _newVersions++;
        return version;
    }

    private FamilyVersion NewVersionRow(FamilyCard card, ScannedFile file, string note, VersionState state) => new()
    {
        Id = Guid.CreateVersion7(),
        CardId = card.Id,
        Hash = file.Hash,
        CatalogHash = file.CatalogHash,
        Size = file.Size,
        RevitYear = file.RevitYear,
        State = state,
        RelativePath = file.RelativePath,
        Note = note,
        ReadState = ReadState.Pending,
        SeenAt = _now
    };

    /// <summary>Где карточка теперь: путь, раздел, имя и состояние — по тому, где лежит файл.</summary>
    private void Place(FamilyCard card, ScannedFile file)
    {
        card.RelativePath = file.RelativePath;
        card.Section = LibraryPaths.Section(file.RelativePath, _reviewFolder);
        card.SetName(LibraryPaths.FamilyName(file.RelativePath));
        card.State = LibraryPaths.IsInReview(file.RelativePath, _reviewFolder) ? CardState.InReview : CardState.Approved;
        card.UpdatedAt = _now;
    }

    private VersionState StateOf(ScannedFile file) =>
        LibraryPaths.IsInReview(file.RelativePath, _reviewFolder) ? VersionState.InReview : VersionState.Approved;

    /// <summary>Случай в «Нужно решение», если такого открытого ещё нет.</summary>
    private void Issue(IssueKind kind, Guid? cardId, ScannedFile file, string text)
    {
        _seenIssues.Add((kind, file.RelativePath.ToLowerInvariant()));
        if (_openIssues.Any(i => i.Kind == kind && i.Hash == file.Hash && i.RelativePath.Equals(file.RelativePath, StringComparison.OrdinalIgnoreCase))
            || _dismissed.Contains((kind, file.Hash, file.RelativePath.ToLowerInvariant())))
            return;

        var issue = new FamilyIssue
        {
            Id = Guid.CreateVersion7(),
            Kind = kind,
            CardId = cardId,
            RelativePath = file.RelativePath,
            Hash = file.Hash,
            CatalogHash = file.CatalogHash,
            RevitYear = file.RevitYear,
            Size = file.Size,
            Text = text,
            CreatedAt = _now
        };
        db.Set<FamilyIssue>().Add(issue);
        _openIssues.Add(issue);
        _newIssues++;
    }

    private string ConflictText(NameConflict conflict)
    {
        var existing = _cards[conflict.ExistingCardId];
        return $"В библиотеке уже есть семейство «{existing.Name}» в «{existing.RelativePath}». Файл «{conflict.File!.RelativePath}» с тем же именем " +
               "и другим содержимым в каталог не попал: в проекте Revit два семейства с одним именем затёрли бы друг друга. " +
               "Переименуйте один из файлов или удалите лишний.";
    }

    private string DuplicateText(Duplicate duplicate)
    {
        var existing = _cards[duplicate.ExistingCardId];
        return $"Файл «{duplicate.File!.RelativePath}» — копия семейства «{existing.Name}» из «{existing.RelativePath}». Вторая карточка не создана. " +
               "Удалите лишнюю копию.";
    }

    private string AmbiguousText(Ambiguous ambiguous)
    {
        var gone = _cards[ambiguous.GoneCardId];
        return $"Файл семейства «{gone.Name}» («{gone.RelativePath}») исчез, и в том же обходе появился «{ambiguous.File!.RelativePath}» с другим содержимым. " +
               $"Неясно, новая это версия «{gone.Name}» или другое семейство. Пока случай не решён, карточка «{gone.Name}» в архив не уходит. " +
               $"Если это новая версия — положите файл на место прежнего под именем «{gone.Name}».";
    }

    private static string Normalize(string relativePath) => relativePath.Replace('/', '\\').TrimStart('\\');

    private static bool IsHash(string hash) => hash.Length == 64 && hash.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');
}
