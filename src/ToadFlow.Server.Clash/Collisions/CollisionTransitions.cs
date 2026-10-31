namespace ToadFlow.Server.Clash.Collisions;

/// <summary>Где элемент коллизии в новой выгрузке.</summary>
public enum ElementPresence
{
    /// <summary>Есть в геометрии.</summary>
    InGeometry,

    /// <summary>Нет в геометрии, но есть в описи модели — скрыт на виде выгрузки.</summary>
    InventoryOnly,

    /// <summary>Нет ни в геометрии, ни в описи — удалён.</summary>
    Absent
}

/// <summary>Почему открытая пара не нашлась в прогоне.</summary>
/// <param name="ModelUnchecked">Модель хотя бы одного элемента в этом цикле не выгрузилась.</param>
/// <param name="PreviousConfigWouldHit">Версия конфигурации сменилась: нашла бы пару прежняя версия на той же геометрии; null — версия та же.</param>
public sealed record GoneFacts(ElementPresence A, ElementPresence B, bool ModelUnchecked, bool? PreviousConfigWouldHit);

/// <summary>Ручной переход статуса недопустим; текст — для пользователя.</summary>
public sealed class CollisionTransitionException(string message) : Exception(message);

/// <summary>Переходы статусов коллизии (спек §9): после прогона и по действию человека.</summary>
public static class CollisionTransitions
{
    /// <summary>Пара найдена прогоном.</summary>
    /// <returns>Новый статус и запись в ленту, если её нужно оставить.</returns>
    public static (CollisionStatus Next, string? Note) Found(CollisionStatus current) => current switch
    {
        CollisionStatus.Fake => (CollisionStatus.Fake, null),
        CollisionStatus.Fixed => (CollisionStatus.Returned, null),
        CollisionStatus.FixedPending => (CollisionStatus.Active, "Отмечена исправленной, но пересечение осталось"),
        _ => (CollisionStatus.Active, null)
    };

    /// <summary>Пара не найдена прогоном.</summary>
    public static CollisionStatus NotFound(CollisionStatus current, GoneFacts facts)
    {
        if (current is CollisionStatus.Fake or CollisionStatus.Fixed) return current;
        // «Не проверена» считается открытой: закрытую ушедшую из-за правил она открыла бы, а ручную отметку «исправлена» стёрла бы.
        if (facts.ModelUnchecked) return current is CollisionStatus.GoneByRules or CollisionStatus.FixedPending ? current : CollisionStatus.Unchecked;
        if (facts.A == ElementPresence.Absent || facts.B == ElementPresence.Absent) return CollisionStatus.Fixed;
        // Ушедшую из-за правил пару прогон той же версии не проверяет: судить, исправлена ли она, не по чему.
        if (current == CollisionStatus.GoneByRules) return current;
        if (facts.A == ElementPresence.InventoryOnly || facts.B == ElementPresence.InventoryOnly) return CollisionStatus.GoneByRules;
        return facts.PreviousConfigWouldHit == true ? CollisionStatus.GoneByRules : CollisionStatus.Fixed;
    }

    /// <summary>Человек ставит «фиктивная» (с примечанием) или «исправлена (ожидает подтверждения)», либо снимает «фиктивная».</summary>
    /// <param name="foundNow">Пара найдена последним прогоном.</param>
    /// <exception cref="CollisionTransitionException">Переход недопустим.</exception>
    public static CollisionStatus Manual(CollisionStatus current, CollisionStatus target, bool foundNow, string? note) => target switch
    {
        CollisionStatus.Fake when current == CollisionStatus.Fake => throw new CollisionTransitionException("Коллизия уже фиктивная"),
        CollisionStatus.Fake when string.IsNullOrWhiteSpace(note) =>
            throw new CollisionTransitionException("Для фиктивной коллизии нужно примечание: почему она фиктивная"),
        CollisionStatus.Fake => CollisionStatus.Fake,
        CollisionStatus.FixedPending when current is CollisionStatus.New or CollisionStatus.Active or CollisionStatus.Returned => CollisionStatus.FixedPending,
        CollisionStatus.FixedPending => throw new CollisionTransitionException("Исправленной можно отметить только открытую коллизию"),
        CollisionStatus.Active when current == CollisionStatus.Fake => foundNow ? CollisionStatus.Active : CollisionStatus.Fixed,
        _ => throw new CollisionTransitionException("Этот статус ставит прогон, а не человек")
    };
}
