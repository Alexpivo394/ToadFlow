namespace ToadFlow.Server.Modules;

/// <summary>
///     Известные модули (собраны в сервер, их таблицы есть в схеме) и включённые настройкой <c>Modules</c>. Выключенный модуль
///     сохраняет таблицы, но у него нет служб, маршрутов, расписаний и обработки итогов заданий.
/// </summary>
public sealed class ModuleSet
{
    public const string Setting = "Modules";

    private readonly Dictionary<string, string> _moduleOfKind;

    public ModuleSet(IEnumerable<IServerModule> known, IConfiguration configuration)
    {
        Known = known.ToList();
        var selected = ParseSetting(configuration[Setting]);
        if (selected is not null)
        {
            var names = Known.Select(m => m.Name).ToHashSet();
            if (selected.FirstOrDefault(name => !names.Contains(name)) is { } unknown)
                throw new InvalidOperationException($"Неизвестный модуль «{unknown}» в MODULES. Допустимые: {string.Join(", ", names)}");
        }

        Enabled = Known.Where(m => selected is null || selected.Contains(m.Name)).ToList();
        _moduleOfKind = Known.SelectMany(m => m.Kinds.Select(kind => (kind, m.Name))).ToDictionary(p => p.kind, p => p.Name);
    }

    public IReadOnlyList<IServerModule> Known { get; }

    public IReadOnlyList<IServerModule> Enabled { get; }

    public bool IsEnabled(string name) => Enabled.Any(m => m.Name == name);

    public bool IsKindEnabled(string kind) => NameOfKind(kind) is { } name && IsEnabled(name);

    /// <summary>Имя модуля по виду задания; вид, которого нет ни у одного известного модуля, — null.</summary>
    public string? NameOfKind(string kind) => _moduleOfKind.GetValueOrDefault(kind);

    public IReadOnlyList<string> KindsOf(string name) => Known.FirstOrDefault(m => m.Name == name)?.Kinds.ToList() ?? [];

    /// <summary>Виды заданий всех включённых модулей.</summary>
    public IReadOnlyList<string> EnabledKinds => Enabled.SelectMany(m => m.Kinds).ToList();

    /// <summary>
    ///     Разбирает настройку <c>Modules</c>: имена через запятую, без учёта регистра и пробелов, пустые и повторы отбрасываются.
    ///     Пусто или не задано — null: включены все известные модули.
    /// </summary>
    public static IReadOnlySet<string>? ParseSetting(string? value)
    {
        var names = (value ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(name => name.ToLowerInvariant()).ToHashSet();
        return names.Count == 0 ? null : names;
    }
}

public sealed record ModuleView(string Name, string Title, bool Enabled);

public static class ModuleSetEndpoints
{
    /// <summary><c>GET /api/modules</c> — известные модули и включён ли каждый; любой роли после входа.</summary>
    public static void MapModuleList(this RouteGroupBuilder members) =>
        members.MapGet("/modules", (ModuleSet modules) =>
            modules.Known.Select(m => new ModuleView(m.Name, m.Title, modules.IsEnabled(m.Name))).ToArray());
}
