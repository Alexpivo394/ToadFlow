using System.Security.Claims;
using Alexpivo394.Revit.Families.Files.Scan;
using Microsoft.EntityFrameworkCore;
using ToadFlow.Server.Auth;
using ToadFlow.Server.Data;
using ToadFlow.Server.Events;
using ToadFlow.Server.Families.Cards;
using ToadFlow.Server.Families.Data;
using ToadFlow.Server.Families.Scan;
using ToadFlow.Server.Families.Search;

namespace ToadFlow.Server.Families.Panel;

/// <summary>Библиотека для панели Revit: где лежат файлы и может ли этот пользователь публиковать.</summary>
public sealed record LibraryInfo(bool Configured, string Root, string ReviewFolder, bool CanPublish);

/// <param name="Path">Полный путь, куда плагин сохранил файл; должен лежать в библиотеке.</param>
/// <param name="CardId">Новая версия этой карточки; null — новое семейство.</param>
/// <param name="RequestId">Заявка, которую закрывает публикация.</param>
public sealed record PublishRequest(string Path, string Hash, Guid? CardId, string? Comment, Guid? RequestId)
{
    /// <summary>Новая версия этой итерации карточки <see cref="CardId" />.</summary>
    public Guid? IterationId { get; init; }

    /// <summary>Новая итерация карточки <see cref="CardId" /> с этим названием (имя её папки).</summary>
    public string? IterationName { get; init; }

    /// <summary>Комментарий новой итерации.</summary>
    public string? IterationComment { get; init; }

    /// <summary>Без оповещения «Обновлено».</summary>
    public bool Silent { get; init; }

    /// <summary>Не пусто — версия итерации финальная, это что проделано.</summary>
    public string? FinalComment { get; init; }
}

/// <param name="ScanStarted">Обход начат сразу; иначе — сразу после текущего.</param>
public sealed record PublishResult(Guid PublicationId, string RelativePath, bool ScanStarted);

/// <param name="Hash">Хеш загруженного файла.</param>
/// <param name="Project">Имя модели, куда загружено.</param>
public sealed record LoadReport(string? Hash, string? Project)
{
    /// <summary>Из какой итерации загружено; null — «Основная».</summary>
    public Guid? IterationId { get; init; }
}

/// <summary>Семейство открытого проекта; <paramref name="CardId" /> и <paramref name="Hash" /> — из метки, если она есть.</summary>
public sealed record ProjectFamilyDto(string Name, Guid? CardId, string? Hash);

public sealed record ProjectStatusRequest(IReadOnlyList<ProjectFamilyDto> Families, bool? Unreviewed);

public enum ProjectFamilyState
{
    /// <summary>Загружена действующая версия карточки.</summary>
    Current,

    /// <summary>Загружена прошлая версия: в библиотеке новее.</summary>
    Outdated,

    /// <summary>Метки нет; если в библиотеке есть семейство с таким именем, оно предложено.</summary>
    NotFromLibrary,

    /// <summary>Карточка из метки убрана в архив.</summary>
    Archived
}

/// <param name="CardId">Карточка из метки или предложенная по имени.</param>
/// <param name="CurrentHash">Хеш действующей версии — его панель сверяет с файлом перед обновлением.</param>
public sealed record ProjectFamilyStatus(
    string Name,
    ProjectFamilyState State,
    Guid? CardId,
    string? CardName,
    string? CurrentHash,
    int? RevitYear,
    string? FullPath,
    DateTimeOffset? UpdatedAt);

public sealed record VersionCommentRequest(string? Comment, bool Mine);

/// <summary>Маршруты панели «Семейства» в Revit: публикация, учёт загрузок, состояние семейств проекта.</summary>
internal static class PanelEndpoints
{
    private const int MaxComment = 2000;
    private const int MaxProjectFamilies = 5000;

    /// <summary>Участникам; публикация — координатору.</summary>
    public static void MapFamiliesPanel(this RouteGroupBuilder families)
    {
        families.MapGet("/library", LibraryAsync);
        families.MapPost("/{id:guid}/loads", LoadAsync);
        families.MapPost("/project-status", ProjectStatusAsync);
        families.MapPost("/publications", PublishAsync).RequireAuthorization(Policies.Operator);
    }

    /// <summary>Наблюдателю и старше: описание и автор версии.</summary>
    public static void MapFamiliesVersions(this RouteGroupBuilder families) =>
        families.MapPut("/versions/{id:guid}/comment", CommentAsync).RequireAuthorization(Policies.Operator);

    private static async Task<LibraryInfo> LibraryAsync(FamiliesSettingsStore store, ClaimsPrincipal user, CancellationToken ct)
    {
        var settings = await store.GetAsync(ct);
        return new LibraryInfo(settings.Configured, settings.Root, settings.ReviewFolder, user.IsStaff());
    }

    private static async Task<IResult> PublishAsync(PublishRequest request, ToadFlowDb db, FamiliesSettingsStore store, LibraryScanner scanner, TimeProvider time,
        ClaimsPrincipal user, IDomainEvents events, CancellationToken ct)
    {
        var settings = await store.GetAsync(ct);
        if (!settings.Configured) return Results.BadRequest(new { error = "Корень библиотеки не задан в параметрах модуля «Семейства»" });
        if (!IsHash(request.Hash) || string.IsNullOrWhiteSpace(request.Path)) return Results.BadRequest(new { error = "Нужны путь и хеш файла" });
        if (RelativeTo(settings.Root, request.Path) is not { } relative)
            return Results.BadRequest(new { error = $"Файл должен лежать в библиотеке «{settings.Root}»" });
        if (!relative.EndsWith(".rfa", StringComparison.OrdinalIgnoreCase) || relative.Split('\\')[0].Equals(LibraryPaths.ServiceFolder, StringComparison.OrdinalIgnoreCase))
            return Results.BadRequest(new { error = "Публикуется только файл семейства .rfa вне служебной папки" });
        if (request.Comment?.Length > MaxComment) return Results.BadRequest(new { error = $"Описание — до {MaxComment} знаков" });

        if (request.FinalComment?.Length > MaxComment || request.IterationComment?.Length > MaxComment)
            return Results.BadRequest(new { error = $"Комментарий — до {MaxComment} знаков" });
        var toIteration = request.IterationId is not null || !string.IsNullOrWhiteSpace(request.IterationName);
        if (!toIteration && !string.IsNullOrWhiteSpace(request.FinalComment))
            return Results.BadRequest(new { error = "Финальной бывает только версия итерации" });
        if (toIteration) return await PublishToIterationAsync(request, relative, settings, db, scanner, time, user, events, ct);
        if (IterationPaths.Parse(relative, settings.IterationsFolder) is not null)
            return Results.BadRequest(new { error = $"В папку «{settings.IterationsFolder}» публикуется только итерация — выберите «В итерацию»" });

        var name = LibraryPaths.FamilyName(relative);
        var key = name.ToLowerInvariant();
        var owner = await db.Set<FamilyCard>().AsNoTracking().FirstOrDefaultAsync(c => c.NameKey == key && c.State != CardState.Archived, ct);
        if (request.CardId is { } cardId)
        {
            var card = await db.Set<FamilyCard>().AsNoTracking().FirstOrDefaultAsync(c => c.Id == cardId, ct);
            if (card is null || card.State == CardState.Archived) return Results.NotFound(new { error = "Семейство не найдено в каталоге" });
            if (!card.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                return Results.BadRequest(new { error = $"Новая версия «{card.Name}» сохраняется под тем же именем" });
        }
        else if (owner is not null)
            return Results.Conflict(new { error = $"Имя «{name}» уже занято семейством в «{owner.RelativePath}». Выберите «новая версия существующего» или другое имя" });

        if (request.RequestId is { } requestId && !await Requests.Requests.IsOpenAsync(db, requestId, ct))
            return Results.BadRequest(new { error = "Заявка не найдена или уже закрыта" });

        var publication = new FamilyPublication
        {
            Id = Guid.CreateVersion7(),
            RelativePath = relative,
            Hash = request.Hash,
            AuthorUserId = user.UserId(),
            Comment = string.IsNullOrWhiteSpace(request.Comment) ? null : request.Comment.Trim(),
            CardId = request.CardId,
            RequestId = request.RequestId,
            Silent = request.Silent,
            CreatedAt = time.GetUtcNow()
        };
        return await SavePublicationAsync(publication, db, scanner, user, events, ct);
    }

    private static async Task<IResult> SavePublicationAsync(FamilyPublication publication, ToadFlowDb db, LibraryScanner scanner, ClaimsPrincipal user,
        IDomainEvents events, CancellationToken ct)
    {
        db.Set<FamilyPublication>().Add(publication);
        await db.SaveChangesAsync(ct);
        events.Publish(new CatalogChanged(user.Actor(), ScanApplier.UiEntity, publication.Id, $"Семейства: опубликовано «{publication.RelativePath}»"));

        var scan = await scanner.RequestAsync(ct);
        return Results.Ok(new PublishResult(publication.Id, publication.RelativePath, scan.Outcome == ScanStart.Started));
    }

    /// <summary>
    ///     Публикация в итерацию — сразу, без папки проверки. Существующая итерация: файл — на её месте, итерация не завершена. Новая: название
    ///     свободно в карточке, файл — в папке новой итерации; итерацию создаст обход, увидев файл.
    /// </summary>
    private static async Task<IResult> PublishToIterationAsync(PublishRequest request, string relative, FamiliesSettings settings, ToadFlowDb db,
        LibraryScanner scanner, TimeProvider time, ClaimsPrincipal user, IDomainEvents events, CancellationToken ct)
    {
        if (request.CardId is not { } cardId) return Results.BadRequest(new { error = "Итерация публикуется для семейства библиотеки — выберите его" });
        var card = await db.Set<FamilyCard>().AsNoTracking().FirstOrDefaultAsync(c => c.Id == cardId, ct);
        if (card is null || card.State == CardState.Archived) return Results.NotFound(new { error = "Семейство не найдено в каталоге" });

        string expected;
        if (request.IterationId is { } iterationId)
        {
            var iteration = await db.Set<FamilyIteration>().AsNoTracking().FirstOrDefaultAsync(i => i.Id == iterationId && i.CardId == cardId, ct);
            if (iteration is null) return Results.NotFound(new { error = "Итерация не найдена" });
            if (iteration.IsFinal) return Results.BadRequest(new { error = $"Итерация «{iteration.Name}» завершена — снимите финал в карточке семейства в вебе" });
            expected = iteration.RelativePath.Length > 0 ? iteration.RelativePath : IterationPaths.PathOf(settings.IterationsFolder, card.Name, iteration.Name);
        }
        else
        {
            var name = request.IterationName!.Trim();
            if (IterationPaths.NameError(name) is { } error) return Results.BadRequest(new { error });
            var key = name.ToLowerInvariant();
            if (await db.Set<FamilyIteration>().AnyAsync(i => i.CardId == cardId && i.NameKey == key, ct))
                return Results.Conflict(new { error = $"Итерация «{name}» у семейства «{card.Name}» уже есть — выберите её из списка" });
            expected = IterationPaths.PathOf(settings.IterationsFolder, card.Name, name);
        }

        if (!relative.Equals(expected, StringComparison.OrdinalIgnoreCase))
            return Results.BadRequest(new { error = $"Файл итерации лежит в «{expected}»" });

        return await SavePublicationAsync(new FamilyPublication
        {
            Id = Guid.CreateVersion7(),
            RelativePath = relative,
            Hash = request.Hash,
            AuthorUserId = user.UserId(),
            Comment = Clean(request.Comment),
            CardId = cardId,
            RequestId = null,
            IterationId = request.IterationId,
            IterationComment = request.IterationId is null ? Clean(request.IterationComment) : null,
            Silent = request.Silent,
            FinalComment = Clean(request.FinalComment),
            CreatedAt = time.GetUtcNow()
        }, db, scanner, user, events, ct);
    }

    private static string? Clean(string? text) => string.IsNullOrWhiteSpace(text) ? null : text.Trim();

    private static async Task<IResult> LoadAsync(Guid id, LoadReport report, ToadFlowDb db, TimeProvider time, ClaimsPrincipal user, CancellationToken ct)
    {
        var card = await db.Set<FamilyCard>().AsNoTracking().SingleOrDefaultAsync(c => c.Id == id, ct);
        if (card is null || !user.CanSee(card, unreviewed: true)) return Results.NotFound();

        var now = time.GetUtcNow();
        var iterationId = report.IterationId is { } chosen && await db.Set<FamilyIteration>().AnyAsync(i => i.Id == chosen && i.CardId == id, ct) ? chosen : (Guid?)null;
        var version = report.Hash is { Length: > 0 } hash
            ? await db.Set<FamilyVersion>().AsNoTracking().Where(v => v.CardId == id && v.Hash == hash && v.IterationId == iterationId).Select(v => (Guid?)v.Id)
                .FirstOrDefaultAsync(ct)
            : null;
        db.Set<FamilyLoad>().Add(new FamilyLoad
        {
            Id = Guid.CreateVersion7(),
            CardId = id,
            VersionId = version,
            IterationId = iterationId,
            UserId = user.UserId(),
            Project = Truncate(report.Project?.Trim() ?? "", 260),
            At = now
        });
        await db.SaveChangesAsync(ct);
        await db.Set<FamilyCard>().Where(c => c.Id == id).ExecuteUpdateAsync(c => c.SetProperty(x => x.LoadCount, x => x.LoadCount + 1).SetProperty(x => x.LastLoadedAt, now), ct);
        return Results.Ok();
    }

    private static async Task<IResult> ProjectStatusAsync(ProjectStatusRequest request, ToadFlowDb db, FamiliesSettingsStore store, ClaimsPrincipal user,
        CancellationToken ct)
    {
        var families = request.Families ?? [];
        if (families.Count > MaxProjectFamilies) return Results.BadRequest(new { error = $"Не больше {MaxProjectFamilies} семейств за раз" });

        var root = (await store.GetAsync(ct)).Root;
        var ids = families.Where(f => f.CardId is not null).Select(f => f.CardId!.Value).Distinct().ToList();
        var keys = families.Where(f => f.CardId is null).Select(f => f.Name.ToLowerInvariant()).Distinct().ToList();
        var stamped = await db.Set<FamilyCard>().AsNoTracking().Where(c => ids.Contains(c.Id)).ToDictionaryAsync(c => c.Id, ct);
        var byName = (await db.Set<FamilyCard>().AsNoTracking().Visible(user, null, request.Unreviewed == true).Where(c => keys.Contains(c.NameKey)).ToListAsync(ct))
            .GroupBy(c => c.NameKey).ToDictionary(g => g.Key, g => g.First());
        var currentIds = stamped.Values.Concat(byName.Values).Select(c => c.CurrentVersionId).OfType<Guid>().Distinct().ToList();
        var currents = await db.Set<FamilyVersion>().AsNoTracking().Where(v => currentIds.Contains(v.Id)).ToDictionaryAsync(v => v.Id, v => v.Hash, ct);

        ProjectFamilyStatus Status(string name, ProjectFamilyState state, FamilyCard? card) =>
            new(name, state, card?.Id, card?.Name, card?.CurrentVersionId is { } current ? currents.GetValueOrDefault(current) : null, card?.RevitYear,
                card is null ? null : CardEndpoints.FullPath(root, card.RelativePath), card?.UpdatedAt);

        var result = new List<ProjectFamilyStatus>(families.Count);
        foreach (var family in families)
        {
            // По метке — состояние для всех: проектировщик видит и «убрано в архив», хотя саму карточку архива ему не открыть.
            if (family.CardId is { } cardId && stamped.TryGetValue(cardId, out var card))
            {
                var state = card.State == CardState.Archived ? ProjectFamilyState.Archived
                    : card.CurrentVersionId is { } current && currents.GetValueOrDefault(current) is { } hash && !hash.Equals(family.Hash, StringComparison.OrdinalIgnoreCase)
                        ? ProjectFamilyState.Outdated
                        : ProjectFamilyState.Current;
                result.Add(Status(family.Name, state, card));
            }
            else
                result.Add(Status(family.Name, ProjectFamilyState.NotFromLibrary, byName.GetValueOrDefault(family.Name.ToLowerInvariant())));
        }

        return Results.Ok(result);
    }

    private static async Task<IResult> CommentAsync(Guid id, VersionCommentRequest request, ToadFlowDb db, ClaimsPrincipal user, IDomainEvents events, CancellationToken ct)
    {
        if (request.Comment?.Length > MaxComment) return Results.BadRequest(new { error = $"Описание — до {MaxComment} знаков" });
        var version = await db.Set<FamilyVersion>().SingleOrDefaultAsync(v => v.Id == id, ct);
        if (version is null) return Results.NotFound();

        version.Comment = string.IsNullOrWhiteSpace(request.Comment) ? null : request.Comment.Trim();
        if (request.Mine) version.AuthorUserId = user.UserId();
        await db.SaveChangesAsync(ct);
        events.Publish(new CatalogChanged(user.Actor(), ScanApplier.UiEntity, version.CardId, "Семейства: описание версии"));
        return Results.Ok();
    }

    /// <summary>Путь от корня библиотеки с <c>\</c>; null — файл вне библиотеки или путь с переходами наверх.</summary>
    public static string? RelativeTo(string root, string path)
    {
        var normalizedRoot = root.Replace('/', '\\').TrimEnd('\\') + "\\";
        var normalized = path.Replace('/', '\\').Trim();
        if (!normalized.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase)) return null;
        var relative = normalized[normalizedRoot.Length..].TrimStart('\\');
        if (relative.Length == 0 || relative.Split('\\').Any(part => part is "" or "." or "..")) return null;
        return relative;
    }

    private static string Truncate(string text, int length) => text.Length <= length ? text : text[..length];

    private static bool IsHash(string? hash) => hash is { Length: 64 } && hash.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');
}
