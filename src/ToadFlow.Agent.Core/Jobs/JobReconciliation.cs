using ToadFlow.Contracts;

namespace ToadFlow.Agent.Jobs;

/// <param name="Cancel">Попытки агента, которых сервер за ним больше не числит (вернул задание в очередь, пока не было связи): бросить.</param>
/// <param name="Unknown">
///     Попытки, которые сервер числит за агентом, а агент о них не знает: ответ на запрос задания не дошёл. Их сразу сдать сбоем
///     инфраструктуры — иначе задание ждало бы конца аренды.
/// </param>
public sealed record ReconciliationPlan(IReadOnlyList<JobAttemptKey> Cancel, IReadOnlyList<JobAttemptKey> Unknown);

/// <summary>Сверка заданий агента со списком сервера — после подключения, переподключения и потерянного ответа на запрос задания.</summary>
public static class JobReconciliation
{
    /// <param name="server">Активные задания по версии сервера; null — сервер такого списка не отдаёт (до 2.0): сверять не с чем.</param>
    public static ReconciliationPlan Plan(IEnumerable<JobAttemptKey> local, IReadOnlyCollection<ActiveJobDto>? server)
    {
        if (server is null) return new ReconciliationPlan([], []);
        var known = server.Select(j => new JobAttemptKey(j.JobId, j.Attempt)).ToHashSet();
        var mine = local.ToHashSet();
        return new ReconciliationPlan(mine.Where(k => !known.Contains(k)).ToList(), known.Where(k => !mine.Contains(k)).ToList());
    }

    /// <summary>Итог для попытки, ответ о которой до агента не дошёл: сервер вернёт задание в очередь сразу.</summary>
    public static JobCompletionDto LostAssignment(JobAttemptKey key) =>
        new(key.JobId, JobOutcome.InfrastructureFailure, "агент не получил задание: ответ сервера потерялся в пути", null, key.Attempt);
}
