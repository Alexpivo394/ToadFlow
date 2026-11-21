using Microsoft.EntityFrameworkCore;
using ToadFlow.Contracts;
using ToadFlow.Server.Data;

namespace ToadFlow.Server.Families.Requests;

/// <summary>
///     Направление BIM («АР/КР», «ОВ/ВК»): кто из координаторов делает заявки каких разделов. Разделы библиотеки — папки каталога,
///     разделы моделей — как в профиле пользователя: по ним угадывается направление заявки, когда раздел библиотеки не указан.
/// </summary>
public sealed class FamilyDirection
{
    public Guid Id { get; set; }

    public string Name { get; set; } = "";

    /// <summary>Разделы библиотеки (<c>FamilyCard.Section</c>).</summary>
    public string[] Sections { get; set; } = [];

    /// <summary>Разделы моделей (<c>User.Disciplines</c>).</summary>
    public string[] Disciplines { get; set; } = [];

    /// <summary>Координаторы направления: им приходят заявки.</summary>
    public Guid[] Coordinators { get; set; } = [];

    /// <summary>Что написать в подробностях заявки — подсказка в форме.</summary>
    public string Hint { get; set; } = "";

    /// <summary>Порядок в списке.</summary>
    public int Order { get; set; }
}

/// <summary>Выбор направления заявки и её получателей.</summary>
public static class Directions
{
    /// <summary>
    ///     Направление по разделу библиотеки; если раздела нет или он ни в одном направлении — по разделам профиля автора, когда они
    ///     дают ровно одно направление. Иначе null — общая очередь.
    /// </summary>
    public static async Task<Guid?> ResolveAsync(ToadFlowDb db, string section, Guid authorId, CancellationToken ct)
    {
        var directions = await db.Set<FamilyDirection>().AsNoTracking().OrderBy(d => d.Order).ToListAsync(ct);
        if (directions.Count == 0) return null;

        if (section.Trim() is { Length: > 0 } wanted
            && directions.FirstOrDefault(d => d.Sections.Contains(wanted, StringComparer.OrdinalIgnoreCase)) is { } bySection)
            return bySection.Id;

        var disciplines = await db.Users.AsNoTracking().Where(u => u.Id == authorId).Select(u => u.Disciplines).SingleOrDefaultAsync(ct) ?? [];
        var byProfile = directions.Where(d => d.Disciplines.Intersect(disciplines, StringComparer.OrdinalIgnoreCase).Any()).ToList();
        return byProfile.Count == 1 ? byProfile[0].Id : null;
    }

    /// <summary>Направление, в которое входит раздел библиотеки; null — ни в одно.</summary>
    public static async Task<Guid?> BySectionAsync(ToadFlowDb db, string section, CancellationToken ct)
    {
        if (section.Trim() is not { Length: > 0 } wanted) return null;
        var directions = await db.Set<FamilyDirection>().AsNoTracking().OrderBy(d => d.Order).ToListAsync(ct);
        return directions.FirstOrDefault(d => d.Sections.Contains(wanted, StringComparer.OrdinalIgnoreCase))?.Id;
    }

    /// <summary>Координаторы направления, действующие сейчас; нет направления или в нём никого — все координаторы и администраторы.</summary>
    public static async Task<IReadOnlyList<Guid>> CoordinatorsAsync(ToadFlowDb db, Guid? directionId, CancellationToken ct)
    {
        var staff = db.Users.AsNoTracking().Where(u => !u.IsDisabled && !u.IsPending && (u.Role == UserRole.Operator || u.Role == UserRole.Admin));
        if (directionId is { } id && await db.Set<FamilyDirection>().AsNoTracking().Where(d => d.Id == id).Select(d => d.Coordinators).SingleOrDefaultAsync(ct) is { Length: > 0 } chosen)
        {
            var active = await staff.Where(u => chosen.Contains(u.Id)).Select(u => u.Id).ToListAsync(ct);
            if (active.Count > 0) return active;
        }

        return await staff.Select(u => u.Id).ToListAsync(ct);
    }
}
