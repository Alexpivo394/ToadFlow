using System;

namespace ToadFlow.Revit.Addin.Logic;

/// <summary>
///     Когда Revit, запущенный агентом, должен закрыться сам: связь с агентом оборвалась и не вернулась за <see cref="Grace" />.
///     Агент, упавший или убитый, его уже не закроет, а Revit держал бы лицензию и память.
/// </summary>
public sealed class PipeLoss
{
    public static readonly TimeSpan Grace = TimeSpan.FromMinutes(5);

    private DateTimeOffset? _lostAt;

    /// <summary>Связи нет: отсчёт идёт с первого обрыва.</summary>
    public void Lost(DateTimeOffset now) => _lostAt ??= now;

    /// <summary>Агент снова на связи — отсчёт сначала.</summary>
    public void Restored() => _lostAt = null;

    public bool ShouldExit(DateTimeOffset now) => _lostAt is { } lostAt && now - lostAt >= Grace;
}
