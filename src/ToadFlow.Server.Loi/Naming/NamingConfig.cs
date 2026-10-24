namespace ToadFlow.Server.Loi.Naming;

/// <summary>
///     Как из имени модели понять заказчика, объект, раздел и часть здания. Имя режется на куски по разделителям, и каждый
///     кусок ищется в справочнике — где бы он ни стоял: стандарты именования у компаний разные, и даже у одной компании
///     порядок кусков плавает. Хранится одним документом в таблице настроек.
/// </summary>
public sealed record NamingConfig
{
    /// <summary>Символы, по которым имя режется на куски.</summary>
    public string Separators { get; init; } = "_";

    /// <summary>Записи справочников: кусок (или несколько кусков) имени → значение поля.</summary>
    public List<NamingEntry> Entries { get; init; } = [];

    /// <summary>Распознаватели кусков по шаблону — для корпусов, секций, версий Revit и прочего, что не перечислить.</summary>
    public List<NamingPattern> Patterns { get; init; } = [];

    /// <summary>
    ///     Регулярные выражения на имя целиком с группами <c>object</c>, <c>discipline</c>, <c>part</c>, <c>customer</c> —
    ///     для строгих стандартов. Срабатывает первое подошедшее; найденное значение ещё проходит через справочник.
    /// </summary>
    public List<string> NameExpressions { get; init; } = [];
}

/// <param name="Field">Поле: <see cref="NamingFields" /> или своё имя, например «Стадия».</param>
/// <param name="Tokens">Куски имени через «+», если значение задают несколько кусков сразу: <c>DON+BNK-DOO</c>.</param>
/// <param name="Value">Значение поля; у <see cref="NamingFields.Ignore" /> не нужно.</param>
/// <param name="Customer">Заказчик объекта — только у <see cref="NamingFields.Object" />.</param>
public sealed record NamingEntry(string Field, string Tokens, string Value = "", string? Customer = null);

/// <param name="Pattern">Регулярное выражение на один кусок имени, целиком.</param>
/// <param name="Value">Значение с подстановками групп: <c>корпус $1</c>, <c>корпуса $1–$2</c>.</param>
public sealed record NamingPattern(string Field, string Pattern, string Value = "");

public static class NamingFields
{
    public const string Object = "object";
    public const string Discipline = "discipline";
    public const string Part = "part";

    /// <summary>Кусок известен, но для аналитики не нужен (юрлицо, версия Revit) — не считается нераспознанным.</summary>
    public const string Ignore = "ignore";

    public static bool IsBuiltIn(string field) => field is Object or Discipline or Part or Ignore;
}
