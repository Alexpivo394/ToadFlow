using System.Text.Json;
using Alexpivo394.Revit.Families.Files.Scan;
using Microsoft.EntityFrameworkCore;
using ToadFlow.Contracts;
using ToadFlow.Contracts.Families;
using ToadFlow.Server.Agents;
using ToadFlow.Server.Data;
using ToadFlow.Server.Data.Entities;
using ToadFlow.Server.Events;
using ToadFlow.Server.Families.Cards;
using ToadFlow.Server.Families.Data;
using ToadFlow.Server.Families.Scan;
using ToadFlow.Server.Modules;
using ToadFlow.Server.Queue;

namespace ToadFlow.Server.Families.Read;

/// <summary>
///     Задания ферме прочитать новое содержимое: обычный запуск с заданиями <see cref="ModuleKinds.FamiliesRead" />, как обновление
///     каталога LOI, — его видно в «Ферме», его можно отменить. Итог разбирает <see cref="CompleteAsync" />.
/// </summary>
public sealed class FamilyReadJobs(ToadFlowDb db, FamiliesSettingsStore settingsStore, ModuleSet modules, PreviewStore previews, RunProgress runs,
    IDomainEvents events, TimeProvider time, ILogger<FamilyReadJobs> logger)
{
    public const int MaxJobsPerRun = 200;
    public const int TimeoutMinutes = 10;
    private const string VanishedYearNote = "на ферме больше нет Revit этой версии — задание поставлено заново";

    /// <summary>
    ///     Ставит задания версиям, которые ждут чтения. Год Revit — год файла, если на ферме есть агент с этим Revit; иначе ближайший
    ///     больший; нет никакого — задание не создаётся, версия ждёт (её подберёт <see cref="SweepAsync" />).
    /// </summary>
    public async Task EnqueueAsync(IReadOnlyCollection<Guid> versionIds, CancellationToken ct)
    {
        if (versionIds.Count == 0) return;
        var settings = await settingsStore.GetAsync(ct);
        if (!settings.Configured) return;
        var years = await YearsAsync(ct);
        if (years.Count == 0) return;

        var ids = versionIds.Distinct().ToList();
        var versions = await db.Set<FamilyVersion>().Where(v => ids.Contains(v.Id) && v.ReadState == ReadState.Pending).ToListAsync(ct);
        var cardIds = versions.Select(v => v.CardId).Distinct().ToList();
        var cards = await db.Set<FamilyCard>().AsNoTracking().Where(c => cardIds.Contains(c.Id) && c.State != CardState.Archived).ToDictionaryAsync(c => c.Id, ct);
        var work = versions.Where(v => cards.ContainsKey(v.CardId) && v.State != VersionState.Rejected)
            .Select(v => (Version: v, Card: cards[v.CardId], Year: Choose(v.RevitYear, years)))
            .Where(w => w.Year is not null)
            .OrderBy(w => w.Card.Name)
            .ToList();
        if (work.Count == 0) return;

        var now = time.GetUtcNow();
        var created = new List<Guid>();
        foreach (var chunk in work.Chunk(MaxJobsPerRun))
        {
            var run = new Run
            {
                Id = Guid.CreateVersion7(),
                Name = chunk.Length == 1 ? $"Чтение семейства {chunk[0].Card.Name}" : $"Чтение семейств: {chunk.Length}",
                ModuleName = modules.NameOfKind(ModuleKinds.FamiliesRead),
                State = RunState.Queued,
                CreatedAt = now
            };
            db.Runs.Add(run);
            created.Add(run.Id);
            foreach (var (version, card, year) in chunk)
            {
                // Действующая версия лежит там, где сейчас файл карточки; кандидат — там, где его видел обход.
                var relative = card.CurrentVersionId == version.Id ? card.RelativePath : version.RelativePath;
                var path = CardEndpoints.FullPath(settings.Root, relative);
                var catalog = version.CatalogHash is null ? null : CardEndpoints.FullPath(settings.Root, LibraryPaths.CatalogPath(relative));
                var job = new Job
                {
                    Id = Guid.CreateVersion7(),
                    RunId = run.Id,
                    ModuleKind = ModuleKinds.FamiliesRead,
                    ModelName = card.Name,
                    ModelPath = path,
                    RevitYear = year,
                    PayloadJson = JsonSerializer.Serialize(new FamilyReadPayload(version.Id, path, version.Hash, catalog), ToadFlowDb.Json),
                    Status = JobStatus.Queued,
                    MaxAttempts = 2,
                    Priority = 0,
                    TimeoutMinutes = TimeoutMinutes,
                    CreatedAt = now
                };
                db.Jobs.Add(job);
                version.ReadJobId = job.Id;
            }
        }

        await db.SaveChangesAsync(ct);
        foreach (var runId in created) events.Publish(new RunChanged(Actor.System, runId));
    }

    /// <summary>
    ///     Раз в несколько минут: версиям, которые ждут чтения без живого задания, ставит задания; задание в очереди на Revit, которого
    ///     на ферме больше нет, заменяет; задание, провалившее все попытки сбоем Revit, превращает в «не удалось прочитать».
    /// </summary>
    public async Task SweepAsync(CancellationToken ct)
    {
        var years = await YearsAsync(ct);
        var pending = await db.Set<FamilyVersion>()
            .Where(v => v.ReadState == ReadState.Pending && v.ReadError == null && v.State != VersionState.Rejected
                        && db.Set<FamilyCard>().Any(c => c.Id == v.CardId && c.State != CardState.Archived))
            .ToListAsync(ct);
        if (pending.Count == 0) return;

        var jobIds = pending.Where(v => v.ReadJobId != null).Select(v => v.ReadJobId!.Value).ToList();
        var jobs = await db.Jobs.Where(j => jobIds.Contains(j.Id)).ToDictionaryAsync(j => j.Id, ct);
        var toEnqueue = new List<Guid>();
        var touchedRuns = new HashSet<Guid>();
        var now = time.GetUtcNow();
        foreach (var version in pending)
        {
            var job = version.ReadJobId is { } id ? jobs.GetValueOrDefault(id) : null;
            switch (job?.Status)
            {
                case null:
                    toEnqueue.Add(version.Id);
                    break;
                case JobStatus.Queued when job.RevitYear is not { } year || !years.Contains(year):
                    job.Status = JobStatus.Cancelled;
                    job.Note = VanishedYearNote;
                    job.FinishedAt = now;
                    touchedRuns.Add(job.RunId);
                    toEnqueue.Add(version.Id);
                    break;
                case JobStatus.Queued or JobStatus.Running:
                    break;
                case JobStatus.Failed:
                    await MarkFailedAsync(version, job.Note ?? "задание чтения не удалось", ct);
                    break;
                default:
                    toEnqueue.Add(version.Id);
                    break;
            }
        }

        await db.SaveChangesAsync(ct);
        foreach (var runId in touchedRuns) await runs.UpdateAsync(runId);
        await EnqueueAsync(toEnqueue, ct);
    }

    /// <summary>Итог задания чтения: содержимое и картинка — версии, а если она действующая — и карточке с поиском.</summary>
    public async Task CompleteAsync(Job job, CancellationToken ct)
    {
        if (job.ModuleKind != ModuleKinds.FamiliesRead) return;
        FamilyReadPayload? payload;
        try
        {
            payload = JsonSerializer.Deserialize<FamilyReadPayload>(job.PayloadJson, ToadFlowDb.Json);
        }
        catch (JsonException)
        {
            return;
        }

        var version = payload is null ? null : await db.Set<FamilyVersion>().SingleOrDefaultAsync(v => v.Id == payload.VersionId, ct);
        // Задание, которое сервер уже заменил другим, итог версии не меняет.
        if (version is null || version.ReadJobId != job.Id) return;

        switch (job.Status)
        {
            case JobStatus.Succeeded:
                await SaveContentAsync(job, version, ct);
                break;
            case JobStatus.Warning:
                // Файл изменился после обхода: эту версию уже не прочитать, а новое содержимое заведёт следующий обход.
                version.ReadError = job.Note ?? "файл изменился после обхода";
                break;
            case JobStatus.Failed:
                await MarkFailedAsync(version, job.Note ?? "задание чтения не удалось", ct);
                break;
            default:
                return; // отменено — версию подберёт SweepAsync
        }

        await db.SaveChangesAsync(ct);
        events.Publish(new CatalogChanged(Actor.System, ScanApplier.UiEntity, version.CardId, $"Семейства: прочитано «{job.ModelName}»"));
    }

    /// <summary>Версии Revit, в которых есть кому прочитать семейство: не отключённые агенты, умеющие чтение, с лимитом экземпляров больше нуля.</summary>
    public async Task<List<int>> YearsAsync(CancellationToken ct) =>
        (await db.Agents.AsNoTracking().Where(a => a.State != AgentState.Disabled).ToListAsync(ct))
        .Where(a => AgentCapabilities.CanRun(a.AgentVersion, a.Modules, ModuleKinds.FamiliesRead))
        .SelectMany(a => a.Limits.Where(l => l.MaxInstances > 0).Select(l => l.Year))
        .Distinct()
        .Order()
        .ToList();

    /// <summary>Год файла, если есть; иначе ближайший больший (старший Revit открывает файлы младших); неизвестный год — новейший.</summary>
    internal static int? Choose(int? fileYear, IReadOnlyList<int> years) =>
        fileYear is { } year ? years.Where(y => y >= year).Select(y => (int?)y).FirstOrDefault() : years.Count > 0 ? years[^1] : null;

    private async Task SaveContentAsync(Job job, FamilyVersion version, CancellationToken ct)
    {
        FamilyReadResult? result = null;
        try
        {
            result = job.ResultJson is { } json ? JsonSerializer.Deserialize<FamilyReadResult>(json, ToadFlowDb.Json) : null;
        }
        catch (JsonException exception)
        {
            logger.LogWarning(exception, "Итог чтения семейства {Job} не разобран", job.Id);
        }

        if (result?.Content is null)
        {
            await MarkFailedAsync(version, "аддин не вернул содержимое семейства", ct);
            return;
        }

        version.ContentJson = JsonSerializer.Serialize(result.Content, ToadFlowDb.Json);
        version.ReadState = ReadState.Done;
        version.ReadError = null;
        if (result.PreviewPngBase64 is { Length: > 0 } preview)
        {
            try
            {
                version.HasPreview = await previews.SaveAsync(version.Hash, Convert.FromBase64String(preview), ct);
            }
            catch (Exception exception) when (exception is FormatException or IOException or UnauthorizedAccessException)
            {
                logger.LogWarning(exception, "Картинка семейства {Name} не сохранена", job.ModelName);
            }
        }

        var card = await db.Set<FamilyCard>().SingleAsync(c => c.Id == version.CardId, ct);
        if (card.CurrentVersionId == version.Id) CardFields.Refresh(card, version);
        await db.Set<FamilyIssue>().Where(i => i.Kind == IssueKind.Unreadable && i.CardId == card.Id && i.Hash == version.Hash && i.ResolvedAt == null)
            .ExecuteUpdateAsync(i => i.SetProperty(x => x.ResolvedAt, time.GetUtcNow()), ct);
        // Содержимое теперь у версии, картинка — в хранилище: в итоге задания они больше не нужны и только раздували бы таблицу заданий.
        await db.Jobs.Where(j => j.Id == job.Id).ExecuteUpdateAsync(j => j.SetProperty(x => x.ResultJson, (string?)null), ct);
    }

    private async Task MarkFailedAsync(FamilyVersion version, string note, CancellationToken ct)
    {
        version.ReadState = ReadState.Failed;
        version.ReadError = note;
        var card = await db.Set<FamilyCard>().AsNoTracking().SingleAsync(c => c.Id == version.CardId, ct);
        var open = await db.Set<FamilyIssue>().AnyAsync(i => i.Kind == IssueKind.Unreadable && i.CardId == card.Id && i.Hash == version.Hash && i.ResolvedAt == null, ct);
        if (open) return;
        var path = card.CurrentVersionId == version.Id ? card.RelativePath : version.RelativePath;
        db.Set<FamilyIssue>().Add(new FamilyIssue
        {
            Id = Guid.CreateVersion7(),
            Kind = IssueKind.Unreadable,
            CardId = card.Id,
            RelativePath = path,
            Hash = version.Hash,
            Text = $"Файл «{path}» не удалось прочитать в Revit: {note}. Проверьте, открывается ли он в Revit; исправленный файл положите на место — " +
                   "следующий обход его возьмёт.",
            CreatedAt = time.GetUtcNow()
        });
    }
}

/// <summary>Раз в 5 минут подбирает версии, которые ждут чтения без задания (например, агент с нужным Revit появился позже).</summary>
public sealed class FamilyReadLoop(IServiceScopeFactory scopes, TimeProvider time, ILogger<FamilyReadLoop> logger) : BackgroundService
{
    private static readonly TimeSpan Every = TimeSpan.FromMinutes(5);

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(Every, time, ct);
                await using var scope = scopes.CreateAsyncScope();
                await scope.ServiceProvider.GetRequiredService<FamilyReadJobs>().SweepAsync(ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "Задания чтения семейств не поставлены");
            }
        }
    }
}
