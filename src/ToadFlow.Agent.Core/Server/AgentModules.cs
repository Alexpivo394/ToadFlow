using ToadFlow.Contracts;

namespace ToadFlow.Agent.Server;

/// <summary>
///     Виды заданий, которые агент сообщает серверу. Аддин лежит в общей папке Revit и заменяется установщиком, только когда его DLL
///     не держит запущенный Revit, — до перезагрузки агент новой версии может работать со старым аддином. Тогда выгрузку для коллизий
///     агент не берёт: старый аддин её не знает и провалил бы модель.
/// </summary>
public static class AgentModules
{
    public static readonly string[] All = [ModuleKinds.Export, ModuleKinds.Loi, ModuleKinds.LoiCatalog, ModuleKinds.ClashExtract];

    public static string[] For(bool addinOutdated) => addinOutdated ? All.Where(m => m != ModuleKinds.ClashExtract).ToArray() : All;

    /// <summary>
    ///     Аддин другой версии, чем агент (сравниваются три числа: 2.0.0 и 2.0.0.0 — одна версия). Нечитаемая версия (сборка
    ///     разработчика, тесты) не считается устаревшей.
    /// </summary>
    public static bool AddinOutdated(string agentVersion, string? addinVersion) =>
        Parse(agentVersion) is { } agent && Parse(addinVersion) is { } addin && (agent.Major, agent.Minor, agent.Build) != (addin.Major, addin.Minor, addin.Build);

    private static Version? Parse(string? version) =>
        version is not null && Version.TryParse(version.Split('-', '+')[0], out var parsed) && parsed.Build >= 0 ? parsed : null;
}
