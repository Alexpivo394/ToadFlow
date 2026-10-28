using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;
using Newtonsoft.Json.Linq;
using Newtonsoft.Json.Serialization;
using ToadFlow.Contracts.Pipe;

namespace ToadFlow.Revit.Addin.Logic;

/// <summary>
///     Сообщения пайпа на стороне аддина (Newtonsoft — работает во всех Revit 2020–2027). Формат тот же, что у агента:
///     camelCase, перечисления строками, одна строка на сообщение.
/// </summary>
public static class AddinPipeJson
{
    public static readonly JsonSerializerSettings Settings = new()
    {
        ContractResolver = new CamelCasePropertyNamesContractResolver(),
        Converters = { new StringEnumConverter() },
        DateParseHandling = DateParseHandling.DateTimeOffset,
        Formatting = Formatting.None,
        NullValueHandling = NullValueHandling.Include
    };

    private static readonly JsonSerializer Serializer = JsonSerializer.Create(Settings);

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

    public static string Serialize(PipeMessage message) => JsonConvert.SerializeObject(message, message.GetType(), Settings);

    /// <exception cref="InvalidDataException">Не JSON или неизвестный вид сообщения.</exception>
    public static PipeMessage Deserialize(string line)
    {
        try
        {
            using var reader = new JsonTextReader(new StringReader(line)) { DateParseHandling = DateParseHandling.DateTimeOffset };
            var json = JObject.Load(reader);
            var kind = (string?)json["type"];
            if (kind is null || !Types.TryGetValue(kind, out var target)) throw new InvalidDataException($"Неизвестное сообщение пайпа: «{kind}»");
            return (PipeMessage)json.ToObject(target, Serializer)!;
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException($"Повреждённое сообщение пайпа: {exception.Message}", exception);
        }
    }

    public static T DeserializeObject<T>(string json) => JsonConvert.DeserializeObject<T>(json, Settings)!;

    public static string SerializeObject(object value) => JsonConvert.SerializeObject(value, Settings);
}
