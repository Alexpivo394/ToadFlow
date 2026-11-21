namespace ToadFlow.Server.Families.Requests;

/// <summary>Где заявка: новая → в работе → готова, «уже есть» или отклонена.</summary>
public enum RequestState
{
    New,

    /// <summary>Есть исполнитель.</summary>
    InWork,

    /// <summary>Семейство сделано: ссылка на карточку.</summary>
    Done,

    /// <summary>Такое семейство уже было в каталоге: ссылка на карточку.</summary>
    Exists,

    /// <summary>Отклонена с причиной.</summary>
    Rejected
}

public enum RequestUrgency
{
    Normal,
    Urgent
}

/// <summary>Заявка на семейство: «не нашёл» в каталоге.</summary>
public sealed class FamilyRequest
{
    public Guid Id { get; set; }

    /// <summary>Номер для людей: «заявка № 12».</summary>
    public int Number { get; set; }

    /// <summary>Что нужно — коротко.</summary>
    public string Title { get; set; } = "";

    /// <summary>Подробности: размеры, производитель, где применяется.</summary>
    public string Text { get; set; } = "";

    /// <summary>Раздел библиотеки («ОВ», «КР»); пусто — не указан.</summary>
    public string Section { get; set; } = "";

    public RequestUrgency Urgency { get; set; }

    /// <summary>Проект, для которого нужно; свободный текст.</summary>
    public string Project { get; set; } = "";

    public RequestState State { get; set; }

    /// <summary>Направление BIM; null — общая очередь.</summary>
    public Guid? DirectionId { get; set; }

    public Guid AuthorUserId { get; set; }

    public Guid? AssigneeUserId { get; set; }

    /// <summary>Готова или «уже есть» — карточка семейства.</summary>
    public Guid? CardId { get; set; }

    /// <summary>Причина отклонения.</summary>
    public string? Reason { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>Последнее изменение: состояние, исполнитель, сообщение, вложение. По нему участник видит «есть новое».</summary>
    public DateTimeOffset UpdatedAt { get; set; }

    public DateTimeOffset? ClosedAt { get; set; }

    /// <summary>Заголовок, текст, раздел и проект в нижнем регистре — для поиска похожих.</summary>
    public string SearchText { get; set; } = "";

    public bool IsOpen => State is RequestState.New or RequestState.InWork;
}

/// <summary>Автор и присоединившиеся: видят изменения заявки в «Мои заявки» и в панели Revit.</summary>
public sealed class FamilyRequestMember
{
    public Guid RequestId { get; set; }

    public Guid UserId { get; set; }

    public DateTimeOffset JoinedAt { get; set; }

    /// <summary>Когда участник последний раз открыл заявку.</summary>
    public DateTimeOffset? SeenAt { get; set; }
}

/// <summary>Сообщение переписки или строка о событии («взял в работу», «готова»).</summary>
public sealed class FamilyRequestMessage
{
    public Guid Id { get; set; }

    public Guid RequestId { get; set; }

    /// <summary>null — строка о событии без автора (закрытие публикацией по обходу).</summary>
    public Guid? AuthorUserId { get; set; }

    public string Text { get; set; } = "";

    /// <summary>Строка о событии, а не сообщение человека.</summary>
    public bool IsEvent { get; set; }

    public DateTimeOffset At { get; set; }
}

/// <summary>Вложение заявки: каталог производителя, чертёж, картинка. Файл — в хранилище модуля, <c>requests/{заявка}/{id}</c>.</summary>
public sealed class FamilyRequestFile
{
    public Guid Id { get; set; }

    public Guid RequestId { get; set; }

    public string FileName { get; set; } = "";

    public string ContentType { get; set; } = "";

    public long Size { get; set; }

    public Guid UploadedBy { get; set; }

    public DateTimeOffset At { get; set; }
}
