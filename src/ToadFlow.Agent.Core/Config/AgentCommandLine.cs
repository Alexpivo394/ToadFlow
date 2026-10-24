namespace ToadFlow.Agent.Config;

/// <summary>
///     Команды агента без окна — для установщика и скриптов:
///     <c>ToadFlow.Agent.exe --register --server http://toadflow:8080 --code TF-XXXX-XXXX</c>.
/// </summary>
public static class AgentCommandLine
{
    /// <returns>Код выхода (0 — успех, 1 — ошибка регистрации, 2 — неверные аргументы); null — команды нет, запускать агента.</returns>
    public static async Task<int?> RunAsync(string[] args, TextWriter output, Registration? registration = null)
    {
        if (!args.Contains("--register", StringComparer.OrdinalIgnoreCase)) return null;

        var server = Value(args, "--server");
        var code = Value(args, "--code");
        if (string.IsNullOrWhiteSpace(server) || string.IsNullOrWhiteSpace(code))
        {
            await output.WriteLineAsync("Использование: ToadFlow.Agent.exe --register --server <адрес сервера> --code <код подключения>");
            return 2;
        }

        try
        {
            var config = await (registration ?? new Registration()).RegisterAsync(server, code, Environment.MachineName);
            AgentConfigStore.Save(config);
            await output.WriteLineAsync($"Агент зарегистрирован на {config.ServerUrl}");
            return 0;
        }
        catch (AgentRegistrationException exception)
        {
            await output.WriteLineAsync(exception.Message);
            return 1;
        }
    }

    private static string? Value(string[] args, string name)
    {
        var index = Array.FindIndex(args, a => string.Equals(a, name, StringComparison.OrdinalIgnoreCase));
        return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
    }
}
