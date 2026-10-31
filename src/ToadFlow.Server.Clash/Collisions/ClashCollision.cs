namespace ToadFlow.Server.Clash.Collisions;

/// <summary>Статус коллизии утверждённой проверки (спек §9).</summary>
public enum CollisionStatus
{
    /// <summary>Пара найдена впервые.</summary>
    New,

    /// <summary>Пара была и снова найдена.</summary>
    Active,

    /// <summary>Была «исправлена» и снова найдена; история сохраняется.</summary>
    Returned,

    /// <summary>Так решил человек; прогоны статус не меняют.</summary>
    Fake,

    /// <summary>Человек отметил, что исправил; для прогона — то же, что «активная».</summary>
    FixedPending,

    /// <summary>Пара больше не пересекается: раздвинули, удалили элемент или элемент выпал из наборов.</summary>
    Fixed,

    /// <summary>Пару отсекла новая версия конфигурации, или элемент скрыт на виде выгрузки.</summary>
    GoneByRules,

    /// <summary>Модель элемента в этом цикле не выгрузилась; пересечение не опровергнуто — коллизия считается открытой.</summary>
    Unchecked
}

/// <summary>Кто правит коллизию.</summary>
public enum CollisionAssignee
{
    Both,
    SideA,
    SideB
}

/// <summary>
///     Коллизия утверждённой проверки части: пара элементов, номер на всю жизнь, статус, «кто правит» и последние сведения
///     о пересечении. Пара каноническая — сначала меньший (модель, UniqueId) в ordinal-порядке.
/// </summary>
public sealed class ClashCollision
{
    public Guid Id { get; set; }
    public Guid PartId { get; set; }
    public int Number { get; set; }
    public CollisionStatus Status { get; set; }
    public CollisionAssignee Assignee { get; set; }

    /// <summary>Пара найдена последним прогоном — для «фиктивная, сейчас не пересекается».</summary>
    public bool FoundNow { get; set; }

    public string ModelA { get; set; } = "";
    public string UniqueIdA { get; set; } = "";
    public long ElementIdA { get; set; }
    public string DisciplineA { get; set; } = "";
    public Guid[] SetsA { get; set; } = [];
    public string CategoryA { get; set; } = "";
    public string? FamilyA { get; set; }
    public string? TypeA { get; set; }
    public string? LevelA { get; set; }
    public string ModelB { get; set; } = "";
    public string UniqueIdB { get; set; } = "";
    public long ElementIdB { get; set; }
    public string DisciplineB { get; set; } = "";
    public Guid[] SetsB { get; set; } = [];
    public string CategoryB { get; set; } = "";
    public string? FamilyB { get; set; }
    public string? TypeB { get; set; }
    public string? LevelB { get; set; }

    /// <summary>Разделы сторон по алфавиту через « × ».</summary>
    public string DisciplinePair { get; set; } = "";

    /// <summary>Ключи проверок (клеток матрицы) последней находки.</summary>
    public string[] Checks { get; set; } = [];

    public double X { get; set; }
    public double Y { get; set; }
    public double Z { get; set; }
    public float DepthMm { get; set; }
    public long FingerprintA { get; set; }
    public long FingerprintB { get; set; }

    /// <summary>Версия конфигурации, которой пара найдена впервые.</summary>
    public int FirstVersion { get; set; }

    public int LastFoundVersion { get; set; }
    public Guid LastFoundRunId { get; set; }

    /// <summary>Прогон, которым пара найдена впервые; пусто у коллизий, найденных до этапа 6.</summary>
    public Guid? FirstRunId { get; set; }


    /// <summary>Возможно, это прежняя коллизия с пересозданным элементом; объединяет человек.</summary>
    public Guid? SuggestedPredecessorId { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>Глубина и точка коллизии в прогоне: пишется при первой находке и при изменении глубины или геометрии.</summary>
public sealed class ClashCollisionSample
{
    /// <summary>Глубина изменилась заметно: больше чем на столько мм.</summary>
    public const float DepthStepMm = 1;

    public Guid CollisionId { get; set; }
    public Guid RunId { get; set; }
    public int Version { get; set; }
    public DateTimeOffset At { get; set; }
    public float DepthMm { get; set; }
    public double X { get; set; }
    public double Y { get; set; }
    public double Z { get; set; }
    public long FingerprintA { get; set; }
    public long FingerprintB { get; set; }
}

public enum CollisionEventKind
{
    Note,
    Status,
    Assignee,
    Found,
    Suggestion,
    Merged
}

/// <summary>Запись ленты коллизии: примечание человека или автоматическая запись.</summary>
public sealed class ClashCollisionEvent
{
    public Guid Id { get; set; }
    public Guid CollisionId { get; set; }
    public DateTimeOffset At { get; set; }

    /// <summary>Кто; null — система.</summary>
    public Guid? UserId { get; set; }

    public CollisionEventKind Kind { get; set; }
    public string Text { get; set; } = "";
    public CollisionStatus? FromStatus { get; set; }
    public CollisionStatus? ToStatus { get; set; }
    public int? Version { get; set; }

    /// <summary>Прогон, сверка которого сделала запись; null — запись человека или запись до этапа 6.</summary>
    public Guid? RunId { get; set; }
}

/// <summary>Коллизии части изменились (прогон, статус, примечание) — экрану утверждённой проверки пора обновиться. В журнал не пишется.</summary>
/// <param name="CollisionId">Изменённая коллизия; null — изменилось много (сверка прогона).</param>
public sealed record ClashCollisionsChanged(ToadFlow.Server.Events.Actor Actor, Guid PartId, Guid? CollisionId) : ToadFlow.Server.Events.DomainEvent(Actor)
{
    public override string UiEntity => "clash-collisions";
    public override string UiId => PartId.ToString();
}
