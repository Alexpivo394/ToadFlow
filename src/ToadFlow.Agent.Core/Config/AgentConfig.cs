using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ToadFlow.Agent.Config;

/// <param name="ServerUrl">Адрес сервера ToadFlow, например http://toadflow:8080.</param>
/// <param name="Key">Ключ агента, выданный при регистрации.</param>
public sealed record AgentConfig(string ServerUrl, Guid AgentId, string Key);

/// <summary>
///     Конфигурация агента в %ProgramData%\ToadFlow\Agent\agent.json. Ключ шифруется DPAPI на уровне машины:
///     файл бесполезен на другом компьютере.
/// </summary>
public static class AgentConfigStore
{
    private const string FileName = "agent.json";
    private static readonly byte[] Entropy = "ToadFlow.Agent.Key"u8.ToArray();

    public static AgentConfig? Load()
    {
        var path = Path.Combine(AgentPaths.Root, FileName);
        if (!File.Exists(path)) return null;

        try
        {
            var stored = JsonSerializer.Deserialize<Stored>(File.ReadAllText(path, Encoding.UTF8), JsonSerializerOptions.Web);
            if (stored is null || string.IsNullOrEmpty(stored.ServerUrl) || string.IsNullOrEmpty(stored.ProtectedKey)) return null;

            var key = Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(stored.ProtectedKey), Entropy, DataProtectionScope.LocalMachine));
            return new AgentConfig(stored.ServerUrl, stored.AgentId, key);
        }
        catch (Exception exception) when (exception is JsonException or FormatException or CryptographicException)
        {
            return null; // файл испорчен или с другой машины — агент попросит зарегистрироваться заново
        }
    }

    public static void Save(AgentConfig config)
    {
        Directory.CreateDirectory(AgentPaths.Root);
        var protectedKey = ProtectedData.Protect(Encoding.UTF8.GetBytes(config.Key), Entropy, DataProtectionScope.LocalMachine);
        var stored = new Stored(config.ServerUrl, config.AgentId, Convert.ToBase64String(protectedKey));
        File.WriteAllText(Path.Combine(AgentPaths.Root, FileName), JsonSerializer.Serialize(stored, JsonSerializerOptions.Web), Encoding.UTF8);
    }

    private sealed record Stored(string ServerUrl, Guid AgentId, string ProtectedKey);
}
