using Microsoft.EntityFrameworkCore;
using ToadFlow.Contracts;
using ToadFlow.Server.Auth;
using ToadFlow.Server.Data;
using ToadFlow.Server.Events;
using ToadFlow.Server.Families.Data;

namespace ToadFlow.Server.Families.Requests;

public sealed record DirectionInput(string Name, string[]? Sections, string[]? Disciplines, Guid[]? Coordinators, string? Hint);

public sealed record DirectionPerson(Guid Id, string Name);

public sealed record DirectionView(Guid Id, string Name, string[] Sections, string[] Disciplines, IReadOnlyList<DirectionPerson> Coordinators, string Hint);

/// <summary>Направления BIM: список видят все (форма заявки показывает, кому она уйдёт), меняет администратор.</summary>
internal static class DirectionEndpoints
{
    private const int MaxName = 60;
    private const int MaxHint = 1000;

    /// <summary>Чтение — в группе участников (и проектировщику); <c>uncovered</c> — координатору.</summary>
    public static void MapFamilyDirections(this RouteGroupBuilder families)
    {
        families.MapGet("/directions", ListAsync);
        families.MapGet("/directions/uncovered", UncoveredAsync).RequireAuthorization(Policies.Operator);
        families.MapPut("/directions", SaveAsync).RequireAuthorization(Policies.Admin);
    }

    public static async Task<List<DirectionView>> ListAsync(ToadFlowDb db, CancellationToken ct)
    {
        var directions = await db.Set<FamilyDirection>().AsNoTracking().OrderBy(d => d.Order).ToListAsync(ct);
        var ids = directions.SelectMany(d => d.Coordinators).Distinct().ToList();
        var names = await db.Users.AsNoTracking().Where(u => ids.Contains(u.Id)).ToDictionaryAsync(u => u.Id, u => u.DisplayName, ct);
        return directions.Select(d => new DirectionView(d.Id, d.Name, d.Sections, d.Disciplines,
            d.Coordinators.Where(names.ContainsKey).Select(c => new DirectionPerson(c, names[c])).ToList(), d.Hint)).ToList();
    }

    /// <summary>Разделы каталога (не архивные карточки), которых нет ни в одном направлении: их заявки пойдут в общую очередь.</summary>
    private static async Task<List<string>> UncoveredAsync(ToadFlowDb db, CancellationToken ct)
    {
        var covered = (await db.Set<FamilyDirection>().AsNoTracking().Select(d => d.Sections).ToListAsync(ct)).SelectMany(s => s)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var sections = await db.Set<FamilyCard>().AsNoTracking().Where(c => c.State != CardState.Archived && c.Section != "").Select(c => c.Section).Distinct()
            .ToListAsync(ct);
        return sections.Where(s => !covered.Contains(s)).Order(StringComparer.Create(System.Globalization.CultureInfo.GetCultureInfo("ru-RU"), false)).ToList();
    }

    /// <summary>Весь список целиком: направления с прежними именами сохраняют id — заявки остаются при них; убранные из списка отвязываются.</summary>
    private static async Task<IResult> SaveAsync(DirectionInput[] input, ToadFlowDb db, HttpContext http, IDomainEvents events, CancellationToken ct)
    {
        var cleaned = input.Select(d => new DirectionInput(d.Name?.Trim() ?? "", Clean(d.Sections), Clean(d.Disciplines), (d.Coordinators ?? []).Distinct().ToArray(),
            d.Hint?.Trim() ?? "")).ToList();
        if (cleaned.Any(d => d.Name.Length is 0 or > MaxName)) return Results.BadRequest(new { error = $"Имя направления — от 1 до {MaxName} знаков" });
        if (cleaned.GroupBy(d => d.Name, StringComparer.OrdinalIgnoreCase).Any(g => g.Count() > 1)) return Results.BadRequest(new { error = "Имена направлений повторяются" });
        if (cleaned.Any(d => d.Hint!.Length > MaxHint)) return Results.BadRequest(new { error = $"Подсказка — до {MaxHint} знаков" });

        var people = cleaned.SelectMany(d => d.Coordinators!).Distinct().ToList();
        var staff = await db.Users.AsNoTracking().Where(u => people.Contains(u.Id) && !u.IsDisabled && (u.Role == UserRole.Operator || u.Role == UserRole.Admin))
            .Select(u => u.Id).ToListAsync(ct);
        if (staff.Count != people.Count) return Results.BadRequest(new { error = "Координатором направления может быть только координатор или администратор" });

        var existing = await db.Set<FamilyDirection>().ToListAsync(ct);
        var kept = new List<FamilyDirection>();
        for (var i = 0; i < cleaned.Count; i++)
        {
            var d = cleaned[i];
            var row = existing.FirstOrDefault(e => string.Equals(e.Name, d.Name, StringComparison.OrdinalIgnoreCase) && !kept.Contains(e));
            if (row is null) db.Set<FamilyDirection>().Add(row = new FamilyDirection { Id = Guid.CreateVersion7() });
            row.Name = d.Name;
            row.Sections = d.Sections!;
            row.Disciplines = d.Disciplines!;
            row.Coordinators = d.Coordinators!;
            row.Hint = d.Hint!;
            row.Order = i;
            kept.Add(row);
        }

        var removed = existing.Except(kept).ToList();
        if (removed.Count > 0)
        {
            var removedIds = removed.Select(r => r.Id).ToList();
            await db.Set<FamilyRequest>().Where(r => r.DirectionId != null && removedIds.Contains(r.DirectionId.Value))
                .ExecuteUpdateAsync(r => r.SetProperty(x => x.DirectionId, (Guid?)null), ct);
            db.Set<FamilyDirection>().RemoveRange(removed);
        }

        await db.SaveChangesAsync(ct);
        events.Publish(new SettingsChanged(http.User.Actor(),
            "Семейства: направления — " + (kept.Count == 0 ? "нет" : string.Join(", ", kept.Select(k => k.Name)))));
        return Results.Ok(await ListAsync(db, ct));
    }

    private static string[] Clean(string[]? values) =>
        (values ?? []).Select(v => v?.Trim() ?? "").Where(v => v.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
}
