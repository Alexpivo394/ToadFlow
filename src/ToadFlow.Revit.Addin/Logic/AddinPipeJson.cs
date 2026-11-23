using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using ToadFlow.Contracts.Pipe;

namespace ToadFlow.Revit.Addin.Logic;

/// <summary>
///     Сообщения пайпа на стороне аддина. Формат тот же, что у агента: camelCase, перечисления строками, одна строка на
///     сообщение.
/// </summary>
public static class AddinPipeJson
{
    /// <summary>
    ///     Как у агента (веб-настройки System.Text.Json), но кириллица не экранируется: пути моделей и имена параметров
    ///     идут по пайпу и в базу как есть, а не шестью знаками на букву.
    /// </summary>
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Converters = { new JsonStringEnumConverter() }
    };

    private static readonly Dictionary<string, Type> Types = new()
    {
        [HelloMessage.Kind] = typeof(HelloMessage),
        [ReadyMessage.Kind] = typeof(ReadyMessage),
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
            var kind = document.RootElement.ValueKind == JsonValueKind.Object
                       && document.RootElement.TryGetProperty("type", out var type)
                       && type.ValueKind == JsonValueKind.String
                ? type.GetString()
                : null;
            if (kind is null || !Types.TryGetValue(kind, out var target)) throw new InvalidDataException($"Неизвестное сообщение пайпа: «{kind}»");
            return (PipeMessage)document.RootElement.Deserialize(target, Options)!;
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException($"Повреждённое сообщение пайпа: {exception.Message}", exception);
        }
    }

    public static T DeserializeObject<T>(string json) => JsonSerializer.Deserialize<T>(json, Options)!;

    public static string SerializeObject(object value) => JsonSerializer.Serialize(value, value.GetType(), Options);
}
