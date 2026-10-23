using System.Text.Json;
using System.Text.Json.Serialization;
using ToadFlow.Contracts.Pipe;

namespace ToadFlow.Agent.Instances;

/// <summary>Сообщения пайпа в строку JSON и обратно — на стороне агента (System.Text.Json).</summary>
public static class PipeJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    private static readonly Dictionary<string, Type> Types = new()
    {
        [HelloMessage.Kind] = typeof(HelloMessage),
        [JobMessage.Kind] = typeof(JobMessage),
        [ProgressMessage.Kind] = typeof(ProgressMessage),
        [AliveMessage.Kind] = typeof(AliveMessage),
        [ResultMessage.Kind] = typeof(ResultMessage),
        [CancelMessage.Kind] = typeof(CancelMessage)
    };

    /// <summary>Одна строка без переводов строк: внутри строк JSON они экранируются.</summary>
    public static string Serialize(PipeMessage message) => JsonSerializer.Serialize(message, message.GetType(), Options);

    /// <exception cref="InvalidDataException">Не JSON или неизвестный вид сообщения.</exception>
    public static PipeMessage Deserialize(string line)
    {
        try
        {
            using var document = JsonDocument.Parse(line);
            var kind = document.RootElement.TryGetProperty("type", out var type) ? type.GetString() : null;
            if (kind is null || !Types.TryGetValue(kind, out var target))
                throw new InvalidDataException($"Неизвестное сообщение пайпа: «{kind}»");

            return (PipeMessage)document.RootElement.Deserialize(target, Options)!;
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException($"Повреждённое сообщение пайпа: {exception.Message}", exception);
        }
    }
}
