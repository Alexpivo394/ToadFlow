namespace ToadFlow.Agent;

/// <summary>Где агент держит конфигурацию и логи: %ProgramData%\ToadFlow\Agent.</summary>
public static class AgentPaths
{
    /// <summary>Только для тестов.</summary>
    internal static string? Override { get; set; }

    public static string Root => Override ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "ToadFlow", "Agent");

    public static string Logs => Path.Combine(Root, "logs");
}
