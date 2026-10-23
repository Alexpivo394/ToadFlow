using Microsoft.EntityFrameworkCore;
using ToadFlow.Contracts;
using ToadFlow.Server.Auth;
using ToadFlow.Server.Data;
using ToadFlow.Server.Data.Entities;
using ToadFlow.Server.Events;

namespace ToadFlow.Server.Sources;

public sealed record SourceRequest(string Name, SourceKind Kind, string Host, int? RevitYear, string? RootPath);

public static class SourceEndpoints
{
    public const int MinYear = 2020;
    public const int MaxYear = 2027;

    public static void MapSources(this RouteGroupBuilder api)
    {
        var sources = api.MapGroup("/sources");
        sources.MapGet("/", async (ToadFlowDb db) => await db.Sources.AsNoTracking().OrderBy(s => s.Name).ToListAsync());

        var admin = sources.MapGroup("").RequireAuthorization(Policies.Admin);
        admin.MapPost("/", async (SourceRequest request, ToadFlowDb db, HttpContext http, IDomainEvents events) =>
        {
            if (Validate(request) is { } error) return Results.BadRequest(new { error });

            var source = new Source { Id = Guid.CreateVersion7() };
            Apply(source, request);
            db.Sources.Add(source);
            await db.SaveChangesAsync();
            events.Publish(new CatalogChanged(http.User.Actor(), "sources", source.Id, $"Добавлен источник «{source.Name}»"));
            return Results.Created($"/api/sources/{source.Id}", source);
        });

        admin.MapPut("/{id:guid}", async (Guid id, SourceRequest request, ToadFlowDb db, HttpContext http, IDomainEvents events) =>
        {
            if (Validate(request) is { } error) return Results.BadRequest(new { error });
            var source = await db.Sources.FindAsync(id);
            if (source is null) return Results.NotFound();

            Apply(source, request);
            await db.SaveChangesAsync();
            events.Publish(new CatalogChanged(http.User.Actor(), "sources", id, $"Изменён источник «{source.Name}»"));
            return Results.Ok(source);
        });

        admin.MapDelete("/{id:guid}", async (Guid id, ToadFlowDb db, HttpContext http, IDomainEvents events) =>
        {
            var source = await db.Sources.FindAsync(id);
            if (source is null) return Results.NotFound();
            if (await db.Schedules.AnyAsync(s => s.SourceId == id))
                return Results.Conflict(new { error = "Источник используется в расписаниях — сначала измените или удалите их" });

            db.Sources.Remove(source);
            await db.SaveChangesAsync();
            events.Publish(new CatalogChanged(http.User.Actor(), "sources", id, $"Удалён источник «{source.Name}»"));
            return Results.NoContent();
        });
    }

    private static void Apply(Source source, SourceRequest request)
    {
        source.Name = request.Name.Trim();
        source.Kind = request.Kind;
        source.Host = request.Host.Trim();
        source.RevitYear = request.Kind == SourceKind.RevitServer ? request.RevitYear : null;
        source.RootPath = (request.RootPath ?? "").Trim().Trim('/');
    }

    private static string? Validate(SourceRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Name)) return "Укажите название";
        if (string.IsNullOrWhiteSpace(request.Host)) return request.Kind == SourceKind.RevitServer ? "Укажите имя Revit Server" : "Укажите папку";

        if (request.Kind == SourceKind.RevitServer)
        {
            if (request.RevitYear is not { } year) return "Для Revit Server укажите версию Revit";
            if (year is < MinYear or > MaxYear) return $"Версия Revit — от {MinYear} до {MaxYear}";
        }
        else if (!IsAbsoluteWindowsPath(request.Host.Trim()))
        {
            return @"Папка — полный путь: \\сервер\папка или X:\папка";
        }

        return null;
    }

    private static bool IsAbsoluteWindowsPath(string path) =>
        path.StartsWith(@"\\", StringComparison.Ordinal) && path.Length > 2
        || path.Length >= 3 && char.IsLetter(path[0]) && path[1] == ':' && path[2] is '\\' or '/';
}
