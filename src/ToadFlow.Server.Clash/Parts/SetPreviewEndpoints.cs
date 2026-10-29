using System.Text.Json;
using System.Text.Json.Serialization;
using Alexpivo394.Revit.Clash.Rules;

namespace ToadFlow.Server.Clash.Parts;

/// <summary>
///     Подсказки редактору наборов по последним слепкам моделей части: «сколько элементов попало», значения свойства в
///     источнике группы, элементы, не попавшие ни в один набор группы. Тело — несохранённый черновик из редактора.
/// </summary>
public static class SetPreviewEndpoints
{
    private static readonly JsonSerializerOptions PropertyJson = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    public static void MapClashSetPreview(this RouteGroupBuilder api)
    {
        api.MapPost("/clash/parts/{id:guid}/preview", PreviewAsync);
        api.MapPost("/clash/parts/{id:guid}/values", ValuesAsync);
        api.MapPost("/clash/parts/{id:guid}/unassigned", UnassignedAsync);
    }

    private sealed record Body(ClashConfiguration Configuration, Guid? SetId, Guid? GroupId, PropertyRef? Property);

    /// <summary>Тело — <c>{ configuration, setId }</c>. Прежняя форма — конфигурация ровно с одним набором — тоже принимается.</summary>
    private static async Task<IResult> PreviewAsync(Guid id, HttpRequest request, PartElements elements, CancellationToken ct)
    {
        var text = await ReadAsync(request, ct);
        ClashConfiguration config;
        Guid setId;
        if (Parse(text) is { SetId: { } wanted } body) (config, setId) = (body.Configuration, wanted);
        else if (DraftEndpoints.TryParse(text) is { Sets.Count: 1 } single) (config, setId) = (single, single.Sets[0].Id);
        else return Results.BadRequest(new { error = "Передайте черновик и набор" });

        if (config.Sets.All(s => s.Id != setId)) return Results.BadRequest(new { error = "Набора нет в черновике" });
        return Results.Ok(SetPreview.Count(config, setId, await elements.LoadAsync(id, ct)));
    }

    private static async Task<IResult> ValuesAsync(Guid id, HttpRequest request, PartElements elements, CancellationToken ct)
    {
        if (Parse(await ReadAsync(request, ct)) is not { GroupId: { } groupId, Property: { } property } body)
            return Results.BadRequest(new { error = "Передайте черновик, группу и свойство" });
        return Results.Ok(PropertyValues.Count(body.Configuration, groupId, property, await elements.LoadAsync(id, ct)));
    }

    private static async Task<IResult> UnassignedAsync(Guid id, HttpRequest request, PartElements elements, CancellationToken ct)
    {
        if (Parse(await ReadAsync(request, ct)) is not { GroupId: { } groupId } body)
            return Results.BadRequest(new { error = "Передайте черновик и группу" });
        return Results.Ok(SetPreview.Unassigned(body.Configuration, groupId, await elements.LoadAsync(id, ct)));
    }

    private static async Task<string> ReadAsync(HttpRequest request, CancellationToken ct)
    {
        using var reader = new StreamReader(request.Body);
        return await reader.ReadToEndAsync(ct);
    }

    // Черновик внутри тела разбирается тем же ConfigurationJson, что и сохранение, — прежний формат тоже читается.
    private static Body? Parse(string text)
    {
        try
        {
            using var document = JsonDocument.Parse(text);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("configuration", out var configuration)) return null;
            if (DraftEndpoints.TryParse(configuration.GetRawText()) is not { } config) return null;

            Guid? Id(string name) => root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String && value.TryGetGuid(out var g) ? g : null;
            var property = root.TryGetProperty("property", out var raw) && raw.ValueKind == JsonValueKind.Object
                ? raw.Deserialize<PropertyRef>(PropertyJson)
                : null;
            return new Body(config, Id("setId"), Id("groupId"), property);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
