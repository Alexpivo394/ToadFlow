namespace ToadFlow.Server.Clash.Parts;

/// <summary>«Сколько элементов попало»: набор считается по последним слепкам моделей части.</summary>
public static class SetPreviewEndpoints
{
    public static void MapClashSetPreview(this RouteGroupBuilder api) => api.MapPost("/clash/parts/{id:guid}/preview", PreviewAsync);

    /// <summary>Тело — конфигурация в формате черновика ровно с одним набором: так набор передаётся тем же форматом, что и черновик.</summary>
    private static async Task<IResult> PreviewAsync(Guid id, HttpRequest request, PartElements elements, CancellationToken ct)
    {
        using var reader = new StreamReader(request.Body);
        if (DraftEndpoints.TryParse(await reader.ReadToEndAsync(ct)) is not { Sets.Count: 1 } config)
            return Results.BadRequest(new { error = "Передайте один набор" });

        var models = await elements.LoadAsync(id, ct);
        return Results.Ok(Alexpivo394.Revit.Clash.Rules.SetPreview.Count(config.Sets[0], models));
    }
}
