using System.Text.Json;

namespace ToadFlow.Clash.Worker;

/// <summary>Спрашивает сервер, включён ли модуль коллизий: если нет, работнику незачем работать.</summary>
internal static class ModuleGate
{
    public const string ClashModule = "clash";

    /// <returns>
    ///     Включён ли модуль по полю <c>modules</c> ответа <c>/api/health</c>; сервер без этого поля (прежней версии) — true;
    ///     сервер недоступен или ответил не JSON — null.
    /// </returns>
    public static async Task<bool?> IsClashEnabledAsync(HttpClient http, CancellationToken ct)
    {
        try
        {
            // 503 (база недоступна) тоже несёт список модулей — код ответа не важен, важно тело.
            using var response = await http.GetAsync("api/health", ct);
            await using var stream = await response.Content.ReadAsStreamAsync(ct);
            using var body = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
            if (body.RootElement.ValueKind != JsonValueKind.Object) return null;
            if (!body.RootElement.TryGetProperty("modules", out var modules) || modules.ValueKind != JsonValueKind.Array) return true;
            return modules.EnumerateArray().Any(m => m.GetString() == ClashModule);
        }
        catch (Exception exception) when (exception is HttpRequestException or JsonException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            return null;
        }
    }
}
