using Microsoft.EntityFrameworkCore;
using ToadFlow.Contracts;
using ToadFlow.Contracts.Families;
using ToadFlow.Server.Agents;
using ToadFlow.Server.Data;
using ToadFlow.Server.Families.Scan;

namespace ToadFlow.Server.Families.Actions;

/// <summary>Могут ли сейчас кнопки веба менять файлы библиотеки; если нет — почему.</summary>
public sealed record FileActionsState(bool Available, string? Reason);

/// <summary>
///     Действия с файлами библиотеки через агента библиотеки (<see cref="IAgentClient.FamilyFileAction" />). После удачного действия
///     сразу просит обход: карточки меняет он, по тем же правилам, что и правки проводником.
/// </summary>
public sealed class LibraryFiles(ToadFlowDb db, FamiliesSettingsStore store, LibraryScanner scanner, AgentRegistry registry, ILogger<LibraryFiles> logger)
{
    private static readonly TimeSpan CallTimeout = TimeSpan.FromMinutes(2);

    public async Task<FileActionsState> StateAsync(CancellationToken ct)
    {
        var settings = await store.GetAsync(ct);
        if (!settings.Configured) return new FileActionsState(false, "корень библиотеки не задан");
        var agent = await scanner.AgentAsync(settings, ct);
        if (agent is null) return new FileActionsState(false, "нет агента библиотеки");
        if (!agent.Online) return new FileActionsState(false, $"агент библиотеки {agent.MachineName} не в сети");
        var modules = await db.Agents.AsNoTracking().Where(a => a.Id == agent.Id).Select(a => a.Modules).SingleOrDefaultAsync(ct);
        return modules?.Contains(ModuleKinds.FamiliesFiles) == true
            ? new FileActionsState(true, null)
            : new FileActionsState(false, $"агент библиотеки {agent.MachineName} старой версии — действия с файлами появились в агенте 3.0");
    }

    /// <returns>Почему не сделано; null — сделано, обход попрошен.</returns>
    public async Task<string?> RunAsync(FamilyFileActionKind kind, string relativePath, string? targetPath, string hash, string? catalogHash, string? replaceHash,
        CancellationToken ct)
    {
        var state = await StateAsync(ct);
        if (!state.Available) return state.Reason;
        var settings = await store.GetAsync(ct);
        var agent = (await scanner.AgentAsync(settings, ct))!;
        if (registry.Client(agent.Id) is not { } client) return $"агент библиотеки {agent.MachineName} не в сети";

        FamilyFileActionResult result;
        try
        {
            result = await client.FamilyFileAction(new FamilyFileActionDto(kind, settings.Root, relativePath, targetPath, hash, catalogHash, replaceHash))
                .WaitAsync(CallTimeout, ct);
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            logger.LogWarning(exception, "Агент библиотеки не выполнил действие {Kind} с «{Path}»", kind, relativePath);
            return $"агент {agent.MachineName} не ответил: {exception.Message}";
        }

        if (result.Error is not null) return result.Error;
        await scanner.RequestAsync(ct);
        return null;
    }
}
