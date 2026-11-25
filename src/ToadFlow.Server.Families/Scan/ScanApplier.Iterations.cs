using Alexpivo394.Revit.Families.Files.Scan;
using Microsoft.EntityFrameworkCore;
using ToadFlow.Server.Families.Data;

namespace ToadFlow.Server.Families.Scan;

/// <summary>
///     Итерации в обходе. Файл папки итераций принадлежит карточке по имени папки семейства и итерации по имени своей папки; правила ядра
///     (конфликт имён, дубль, перенос) на него не действуют.
/// </summary>
public sealed partial class ScanApplier
{
    private readonly Dictionary<Guid, FamilyIteration> _iterations = [];
    private string _iterationsFolder = FamiliesSettings.DefaultIterationsFolder;

    /// <summary>Действующая версия каждой итерации до обхода — для оповещений.</summary>
    private Dictionary<Guid, Guid?> _iterationCurrentBefore = [];

    private async Task LoadIterationsAsync(CancellationToken ct)
    {
        var cardIds = _cards.Keys.ToList();
        var iterations = await db.Set<FamilyIteration>().Where(i => cardIds.Contains(i.CardId)).ToListAsync(ct);
        foreach (var iteration in iterations) _iterations[iteration.Id] = iteration;
        var currentIds = iterations.Select(i => i.CurrentVersionId).OfType<Guid>().ToList();
        foreach (var version in await db.Set<FamilyVersion>().Where(v => currentIds.Contains(v.Id)).ToListAsync(ct)) _versions[version.Id] = version;
        _iterationCurrentBefore = iterations.ToDictionary(i => i.Id, i => i.CurrentVersionId);
    }

    private void ApplyIterations(IReadOnlyList<ScannedFile> files, bool certain)
    {
        var seen = new HashSet<Guid>();
        var unknown = new List<(FamilyCard Card, IterationPath Path, ScannedFile File)>();
        var cardsByName = _cards.Values.Where(c => c.State != CardState.Archived).GroupBy(c => c.NameKey).ToDictionary(g => g.Key, g => g.First());

        foreach (var file in files.OrderBy(f => f.RelativePath, StringComparer.OrdinalIgnoreCase))
        {
            var path = IterationPaths.Parse(file.RelativePath, _iterationsFolder)!;
            var card = path.Valid ? cardsByName.GetValueOrDefault(path.Family.ToLowerInvariant()) : null;
            if (card is null)
            {
                Issue(IssueKind.MisplacedIteration, null, file, MisplacedText(file, path));
                continue;
            }

            var key = path.Iteration.ToLowerInvariant();
            if (_iterations.Values.FirstOrDefault(i => i.CardId == card.Id && i.NameKey == key) is { } iteration)
            {
                seen.Add(iteration.Id);
                Take(card, iteration, file);
            }
            else unknown.Add((card, path, file));
        }

        foreach (var (card, path, file) in unknown)
        {
            // Папку итерации переименовали: прежней итерации в отчёте нет, а содержимое — её действующее.
            var renamed = certain
                ? _iterations.Values.FirstOrDefault(i => i.CardId == card.Id && !seen.Contains(i.Id) && i.RemovedAt is null
                                                         && i.CurrentVersionId is { } currentId && _versions.TryGetValue(currentId, out var current)
                                                         && current.Hash == file.Hash && current.CatalogHash == file.CatalogHash)
                : null;
            var iteration = renamed ?? NewIteration(card, path.Iteration);
            iteration.SetName(path.Iteration);
            seen.Add(iteration.Id);
            Take(card, iteration, file);
        }

        if (!certain) return;
        foreach (var iteration in _iterations.Values.Where(i => !seen.Contains(i.Id) && i.RemovedAt is null && i.CurrentVersionId is not null))
        {
            iteration.RemovedAt = _now;
            iteration.UpdatedAt = _now;
        }
    }

    private FamilyIteration NewIteration(FamilyCard card, string name)
    {
        var iteration = new FamilyIteration { Id = Guid.CreateVersion7(), CardId = card.Id, CreatedAt = _now, UpdatedAt = _now };
        iteration.SetName(name);
        db.Set<FamilyIteration>().Add(iteration);
        _iterations[iteration.Id] = iteration;
        return iteration;
    }

    /// <summary>Файл итерации на месте: новое содержимое — новая версия; итерация не убрана; путь — где файл сейчас.</summary>
    private void Take(FamilyCard card, FamilyIteration iteration, ScannedFile file)
    {
        var current = iteration.CurrentVersionId is { } currentId ? _versions.GetValueOrDefault(currentId) : null;
        if (current is null || current.Hash != file.Hash || current.CatalogHash != file.CatalogHash)
        {
            var version = NewVersionRow(card, file, current is not null && current.Hash == file.Hash ? CatalogNote : "", VersionState.Approved);
            version.IterationId = iteration.Id;
            db.Set<FamilyVersion>().Add(version);
            _versions[version.Id] = version;
            _toRead.Add(version.Id);
            _touched.Add(version);
            _newVersions++;
            iteration.CurrentVersionId = version.Id;
            iteration.UpdatedAt = _now;
            if (iteration.IsFinal)
                Issue(IssueKind.ChangedAfterFinal, card.Id, file,
                    $"Файл завершённой итерации «{iteration.Name}» семейства «{card.Name}» изменён: «{file.RelativePath}». Итерация остаётся завершённой — " +
                    "посмотрите правку; если итерацию нужно продолжать, снимите финал в карточке.");
        }
        else if (current.RelativePath != file.RelativePath) current.RelativePath = file.RelativePath;

        if (iteration.RelativePath != file.RelativePath || iteration.RemovedAt is not null) iteration.UpdatedAt = _now;
        iteration.RelativePath = file.RelativePath;
        iteration.RemovedAt = null;
    }

    /// <summary>Публикация привязалась к версии итерации: комментарий итерации, финал.</summary>
    private void PublishToIteration(FamilyPublication publication, FamilyVersion version)
    {
        if (version.IterationId is not { } iterationId || !_iterations.TryGetValue(iterationId, out var iteration)) return;
        publication.IterationId ??= iterationId;
        if (publication.IterationComment is { Length: > 0 } comment && string.IsNullOrEmpty(iteration.Comment)) iteration.Comment = comment;
        if (publication.FinalComment is { Length: > 0 } final)
        {
            iteration.FinalVersionId = version.Id;
            iteration.FinalComment = final;
            iteration.FinalByUserId = publication.AuthorUserId;
            iteration.FinalAt = _now;
        }
    }

    private string MisplacedText(ScannedFile file, IterationPath path)
    {
        var pattern = $@"{_iterationsFolder}\<семейство>\<итерация>\<семейство>.rfa";
        return path.Valid
            ? $"Файл «{file.RelativePath}» лежит в папке итераций, но семейства «{path.Family}» в библиотеке нет. В каталог он не попал. " +
              "Итерация делается от семейства библиотеки: проверьте имя папки семейства."
            : $"Файл «{file.RelativePath}» лежит в папке итераций не по шаблону «{pattern}». В каталог он не попал. " +
              "Переложите его в папку итерации и назовите как семейство.";
    }
}
