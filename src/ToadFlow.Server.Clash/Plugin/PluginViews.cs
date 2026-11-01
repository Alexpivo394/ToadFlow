namespace ToadFlow.Server.Clash.Plugin;

/// <param name="LastApprovedRunAt">Конец последнего удачного прогона утверждённой версии; null — не было.</param>
public sealed record ModelPartView(Guid Id, string Name, DateTimeOffset? LastApprovedRunAt);

/// <param name="Key">Ключ модели в коллизиях (<c>ClashModel.SnapshotKey</c>).</param>
/// <param name="Discipline">Раздел модели в первой утверждённой части (с поправкой); null — модель ни в одной утверждённой части.</param>
/// <param name="Parts">Утверждённые части, где модель входит в состав.</param>
public sealed record ModelMatchView(string Key, Guid Id, string Name, string ModelPath, string? Discipline, IReadOnlyList<ModelPartView> Parts);

/// <param name="By"><c>path</c> — найдено по пути, <c>name</c> — по имени файла, <c>none</c> — не найдено.</param>
public sealed record ModelResolveView(string By, IReadOnlyList<ModelMatchView> Matches);
