using ToadFlow.Contracts;

namespace ToadFlow.Server.Agents;

/// <summary>Что умеет агент своей версии. Агент и аддин ставятся одним установщиком, поэтому версия агента — это и версия аддина.</summary>
public static class AgentCapabilities
{
    /// <summary>
    ///     Агент передаёт аддину вид задания. Агенты 1.0.x его не передают, и их аддин принял бы любое задание за выгрузку
    ///     без форматов — открыл бы модель и отчитался «успешно», ничего не сделав. Такие агенты получают только выгрузку.
    ///     Версия разработческой сборки (0.x) или нечитаемая — считается свежей.
    /// </summary>
    public static bool KnowsModuleKind(string agentVersion)
    {
        var prefix = agentVersion.Split('-', '+')[0];
        return !Version.TryParse(prefix, out var version) || !(version.Major == 1 && version.Minor == 0);
    }

    /// <summary>Выгрузку для коллизий умеют агенты с 1.8: аддин выгружает геометрию, агент загружает слепок на сервер.</summary>
    private static readonly Version ClashExtractSince = new(1, 8);

    /// <summary>С какой версии агент умеет задания этого вида — для подсказки «обновите агента».</summary>
    public static string MinimumVersion(string moduleKind) => moduleKind == ModuleKinds.ClashExtract ? "1.8" : "1.1";

    /// <summary>Может ли агент этой версии взять задание этого вида.</summary>
    public static bool CanRun(string agentVersion, string moduleKind) => moduleKind switch
    {
        ModuleKinds.Export => true,
        ModuleKinds.ClashExtract => AtLeast(agentVersion, ClashExtractSince),
        _ => KnowsModuleKind(agentVersion)
    };

    /// <summary>Версия не ниже <paramref name="since" />; сборка разработчика (0.x) или нечитаемая версия — свежая.</summary>
    private static bool AtLeast(string agentVersion, Version since)
    {
        var prefix = agentVersion.Split('-', '+')[0];
        return !Version.TryParse(prefix, out var version) || version.Major == 0 || version >= since;
    }
}
