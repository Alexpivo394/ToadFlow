namespace ToadFlow.Agent.Jobs;

/// <summary>
///     Попытка задания: по ней агент различает две попытки одного задания на одной машине (сервер вернул задание в очередь, пока
///     агент был без связи, и выдал его снова) — у каждой свои отмена, запись в списке работающих и папка слепка.
/// </summary>
public readonly record struct JobAttemptKey(Guid JobId, int Attempt);
