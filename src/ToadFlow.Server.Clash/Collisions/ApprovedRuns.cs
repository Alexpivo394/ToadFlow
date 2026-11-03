using Alexpivo394.Revit.Clash.Rules;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using ToadFlow.Contracts.Clash;
using ToadFlow.Server.Clash.Engine;
using ToadFlow.Server.Clash.Parts;
using ToadFlow.Server.Data;
using ToadFlow.Server.Events;

namespace ToadFlow.Server.Clash.Collisions;

/// <summary>Утверждённый прогон части не поставлен: конфигурацию нельзя прогнать — в «События системы».</summary>
public sealed record ClashApprovedRunSkipped(Actor Actor, Guid PartId, string PartName, string Reason) : DomainEvent(Actor)
{
    public override string Describe() => $"Прогон утверждённой проверки части {PartName} не поставлен: {Reason}";
    public override string UiEntity => "clash-parts";
    public override string UiId => PartId.ToString();
}

/// <summary>Постановка прогона утверждённой версии части в очередь движка: снимок утверждённой конфигурации и последних выгрузок.</summary>
public sealed class ApprovedRuns(ToadFlowDb db, PartModels models, TimeProvider time, IDomainEvents events, ILogger<ApprovedRuns> logger)
{
    /// <summary>
    ///     Ставит прогон; если утверждённый прогон части уже ждёт в очереди — возвращает его. Часть без утверждённой версии
    ///     или с конфигурацией, которую нельзя прогнать, — null, причина уходит в «События системы».
    /// </summary>
    /// <param name="triggerRunId">Запуск расписания, после выгрузок которого ставится прогон; null — вручную.</param>
    /// <param name="uncheckedModels">Модели, которые в этом цикле не выгрузились.</param>
    public async Task<EngineRun?> EnqueueAsync(Guid partId, Guid? triggerRunId, IReadOnlyCollection<Guid> uncheckedModels, Actor actor, CancellationToken ct)
    {
        var part = await db.Set<ClashPart>().AsNoTracking().SingleOrDefaultAsync(p => p.Id == partId, ct);
        if (part?.ApprovedVersion is not { } version) return null;
        if (await QueuedAsync(partId, ct) is { } queued)
        {
            if (queued.Version == version) return queued;
            // Ждущий прогон прежней версии не нужен: утверждена новая.
            await db.Set<EngineRun>().Where(r => r.Id == queued.Id && r.State == EngineRunState.Queued)
                .ExecuteUpdateAsync(r => r.SetProperty(x => x.State, EngineRunState.Cancelled).SetProperty(x => x.FinishedAt, time.GetUtcNow()), ct);
        }

        var stored = await db.Set<ClashPartVersion>().AsNoTracking().SingleAsync(v => v.PartId == partId && v.Version == version, ct);
        var json = stored.ConfigurationJson;
        // Модели и разделы — утверждённые: правка состава части до нового утверждения на прогоны версии не влияет.
        var partModels = await models.OfVersionAsync(partId, VersionModels.Parse(stored.ModelsJson), ct);
        var problems = DraftRunChecks.Problems(partModels, ConfigurationJson.Deserialize(json));
        if (problems.Count > 0)
        {
            logger.LogWarning("Утверждённый прогон части {Part} не поставлен: {Problems}", part.Name, string.Join("; ", problems));
            events.Publish(new ClashApprovedRunSkipped(actor, partId, part.Name, string.Join("; ", problems)));
            return null;
        }

        var run = new EngineRun
        {
            Id = Guid.CreateVersion7(),
            PartId = partId,
            Kind = EngineRunKind.Approved,
            Version = version,
            TriggerRunId = triggerRunId,
            StartedBy = actor.Kind == ToadFlow.Server.Data.Entities.ActorKind.User ? actor.Name : null,
            UncheckedModels = [.. uncheckedModels.Distinct()],
            PreviousRunId = part.ApprovedRunId,
            State = EngineRunState.Queued,
            ConfigurationJson = json,
            Models = partModels.Select(m => new EngineRunModel(m.ModelId, m.Name, m.Discipline!, m.Latest!.Id, m.Latest.GeometryBytes, m.Latest.ParametersBytes))
                .ToList(),
            CreatedAt = time.GetUtcNow()
        };
        db.Set<EngineRun>().Add(run);
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException exception) when (exception.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            // Тот же прогон только что поставил другой запрос: ждущий утверждённый прогон части один.
            db.Entry(run).State = EntityState.Detached;
            return await QueuedAsync(partId, ct);
        }

        events.Publish(new ClashDraftChanged(actor, partId));
        return run;
    }

    /// <summary>Модели, которые прогонит утверждённая версия части; часть не утверждалась — пусто.</summary>
    public async Task<IReadOnlyList<PartModelView>> ModelsAsync(Guid partId, CancellationToken ct)
    {
        var version = await db.Set<ClashPart>().Where(p => p.Id == partId).Select(p => p.ApprovedVersion).SingleOrDefaultAsync(ct);
        if (version is null) return [];
        var json = await db.Set<ClashPartVersion>().Where(v => v.PartId == partId && v.Version == version).Select(v => v.ModelsJson).SingleAsync(ct);
        return await models.OfVersionAsync(partId, VersionModels.Parse(json), ct);
    }

    private Task<EngineRun?> QueuedAsync(Guid partId, CancellationToken ct) =>
        db.Set<EngineRun>().AsNoTracking()
            .SingleOrDefaultAsync(r => r.PartId == partId && r.Kind == EngineRunKind.Approved && r.State == EngineRunState.Queued, ct);
}
