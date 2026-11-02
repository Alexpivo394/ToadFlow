namespace ToadFlow.Server.Clash.Plugin;

/// <param name="LastApprovedRunAt">Конец последнего удачного прогона утверждённой версии; null — не было.</param>
public sealed record ModelPartView(Guid Id, string Name, DateTimeOffset? LastApprovedRunAt);

/// <param name="Key">Ключ модели в коллизиях (<c>ClashModel.SnapshotKey</c>).</param>
/// <param name="Discipline">Раздел модели в первой утверждённой части (с поправкой); null — модель ни в одной утверждённой части.</param>
/// <param name="Parts">Утверждённые части, где модель входит в состав.</param>
public sealed record ModelMatchView(string Key, Guid Id, string Name, string ModelPath, string? Discipline, IReadOnlyList<ModelPartView> Parts);

/// <param name="By"><c>path</c> — найдено по пути, <c>name</c> — по имени файла, <c>none</c> — не найдено.</param>
public sealed record ModelResolveView(string By, IReadOnlyList<ModelMatchView> Matches);

/// <summary>Значение фильтра с числом строк выборки без учёта этого же фильтра.</summary>
public sealed record FacetView(string Value, string Label, int Count);

/// <param name="Pairs">Пары разделов в порядке справочника.</param>
/// <param name="Levels">Уровни снизу вверх; без уровня — <c>-</c>, последним.</param>
/// <param name="Statuses"><c>open</c>, <c>fake</c>, <c>fixed</c>, <c>all</c>.</param>
public sealed record ModelFacets(IReadOnlyList<FacetView> Pairs, IReadOnlyList<FacetView> Levels, IReadOnlyList<FacetView> Statuses);

/// <param name="Side">Сторона открытой модели: <c>A</c>, <c>B</c> или <c>AB</c>, если обе стороны из неё.</param>
/// <param name="MineToFix">Правит сторона этой модели или обе.</param>
/// <param name="TakenBy">Кто последним сменил «кто правит»; null — никто.</param>
public sealed record ModelCollisionRow(
    ToadFlow.Server.Clash.Collisions.CollisionRow Collision,
    Guid PartId,
    string PartName,
    string Side,
    bool MineToFix,
    string? TakenBy,
    DateTimeOffset? TakenAt,
    bool Unseen = false);

/// <param name="Total">Строки выборки со всеми фильтрами.</param>
/// <param name="Mine">«Мне править» — без учёта фильтра группы.</param>
/// <param name="Other">«Взяла другая сторона» — без учёта фильтра группы.</param>
/// <param name="NewSinceVisit">Открытые коллизии модели, найденные или вернувшиеся после прошлого захода пользователя; без учёта фильтров.</param>
/// <param name="NewForMe">Открытые коллизии модели, которые пользователь не открывал с тех пор, как они появились или вернулись; без учёта фильтров.</param>
public sealed record ModelCollisionPage(int Total, int Mine, int Other, IReadOnlyList<ModelCollisionRow> Items, ModelFacets Facets, int NewSinceVisit,
    int NewForMe = 0);
