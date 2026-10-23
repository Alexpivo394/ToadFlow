using System.Net.Http.Json;
using System.Text.Json;

namespace ToadFlow.Agent.Config;

public sealed class AgentRegistrationException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>Регистрация агента одноразовым кодом из «Настройки → Агенты».</summary>
/// <param name="handler">Для тестов — обработчик тестового сервера; null — обычная сеть.</param>
public sealed class Registration(HttpMessageHandler? handler = null)
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    public async Task<AgentConfig> RegisterAsync(string serverUrl, string code, string machineName, CancellationToken ct = default)
    {
        var baseUrl = serverUrl.Trim().TrimEnd('/');
        using var client = handler is null ? new HttpClient() : new HttpClient(handler, disposeHandler: false);
        client.Timeout = Timeout;

        HttpResponseMessage response;
        try
        {
            response = await client.PostAsJsonAsync($"{baseUrl}/api/agents/register", new { code = code.Trim(), machineName }, ct);
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or UriFormatException or InvalidOperationException)
        {
            throw new AgentRegistrationException($"Не удалось связаться с сервером {baseUrl}: {exception.Message}", exception);
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
                throw new AgentRegistrationException(await ErrorAsync(response, ct));

            var body = await response.Content.ReadFromJsonAsync<Registered>(JsonSerializerOptions.Web, ct)
                       ?? throw new AgentRegistrationException("Сервер вернул пустой ответ");
            return new AgentConfig(baseUrl, body.AgentId, body.Key);
        }
    }

    private static async Task<string> ErrorAsync(HttpResponseMessage response, CancellationToken ct)
    {
        try
        {
            var error = await response.Content.ReadFromJsonAsync<ErrorBody>(JsonSerializerOptions.Web, ct);
            if (!string.IsNullOrEmpty(error?.Error)) return error.Error;
        }
        catch (JsonException)
        {
            // не JSON
        }

        return $"Сервер отказал в регистрации ({(int)response.StatusCode})";
    }

    private sealed record Registered(Guid AgentId, string Key);

    private sealed record ErrorBody(string? Error);
}
