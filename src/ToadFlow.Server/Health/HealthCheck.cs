namespace ToadFlow.Server.Health;

/// <summary>
///     Режим <c>--healthcheck</c> для healthcheck контейнера: в образе aspnet нет curl, поэтому проверку делает сам сервер —
///     запрос к своему <c>/api/health</c>. Код выхода 0 — здоров, 1 — нет.
/// </summary>
public static class HealthCheck
{
    public const string Argument = "--healthcheck";

    public static async Task<int> RunAsync(string? url = null)
    {
        url ??= $"http://localhost:{Port()}/api/health";
        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
            using var response = await client.GetAsync(url);
            return response.IsSuccessStatusCode ? 0 : 1;
        }
        catch (Exception)
        {
            return 1;
        }
    }

    /// <summary>Порт, который слушает сервер в контейнере: первый из ASPNETCORE_HTTP_PORTS, иначе 8080 (как в образе aspnet).</summary>
    private static string Port() =>
        Environment.GetEnvironmentVariable("ASPNETCORE_HTTP_PORTS")?.Split([';', ','], StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.Trim() is
            { Length: > 0 } port
            ? port
            : "8080";
}
