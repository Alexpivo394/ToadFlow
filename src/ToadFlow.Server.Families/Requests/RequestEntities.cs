namespace ToadFlow.Server.Families.Requests;

/// <summary>
///     Где заявка: новая → в работе (⇄ ждёт автора) → на приёмке → готова или «уже есть»; либо отклонена или отозвана. Хранится числом:
///     новые значения — только в конец.
/// </summary>
public enum RequestState
{
    New,

    /// <summary>Есть исполнитель.</summary>
    InWork,

    /// <summary>Принята: семейство сделано, ссылка на карточку.</summary>
    Done,

    /// <summary>Принята: подошло семейство, которое уже было в каталоге.</summary>
    Exists,

    /// <summary>Отклонена с причиной.</summary>
    Rejected,

    /// <summary>Исполнитель задал вопрос — ход автора.</summary>
    NeedInfo,

    /// <summary>Сделано или найдено — автор принимает («Подходит» / «Не то»).</summary>
    Review,

    /// <summary>Автор отозвал.</summary>
    Withdrawn
}

/// <summary>Срочность прежних версий API (панель ToadTools 4.4): «срочно» — то же, что «блокирует выдачу».</summary>
public enum RequestUrgency
{
    Normal,
    Urgent
}

public enum RequestKind
{
    /// <summary>Нужно новое семейство.</summary>
    New,

    /// <summary>Проблема в семействе каталога: <see cref="FamilyRequest.TargetCardId" />.</summary>
    Fix
}

/// <summary>Чем кончится приёмка: «готова» (сделали) или «уже есть» (нашли существующее).</summary>
public enum RequestResolution
{
    Made,
    Existed
}

/// <summary>Заявка на семейство: «не нашёл» в каталоге или «в семействе проблема».</summary>
public sealed class FamilyRequest
{
    public Guid Id { get; set; }

    /// <summary>Номер для людей: «заявка № 12».</summary>
    public int Number { get; set; }

    public RequestKind Kind { get; set; }

    /// <summary>Что нужно — коротко.</summary>
    public string Title { get; set; } = "";

    /// <summary>Подробности: размеры, производитель, где применяется.</summary>
    public string Text { get; set; } = "";

    /// <summary>Раздел библиотеки («ОВ», «КР»); пусто — не указан.</summary>
    public string Section { get; set; } = "";

    /// <summary>Без семейства не выпустить документацию: такие заявки — первыми в очереди.</summary>
    public bool BlocksIssue { get; set; }

    /// <summary>К какому числу нужно; null — без срока.</summary>
    public DateOnly? NeededBy { get; set; }

    /// <summary>К какому числу обещал исполнитель.</summary>
    public DateOnly? PromisedBy { get; set; }

    /// <summary>Версия Revit проекта, для которого нужно.</summary>
    public int? RevitYear { get; set; }

    /// <summary>Проект, для которого нужно; свободный текст.</summary>
    public string Project { get; set; } = "";

    public RequestState State { get; set; }

    /// <summary>Направление BIM; null — общая очередь.</summary>
    public Guid? DirectionId { get; set; }

    public Guid AuthorUserId { get; set; }

    public Guid? AssigneeUserId { get; set; }

    /// <summary>Результат (на приёмке и после): карточка семейства.</summary>
    public Guid? CardId { get; set; }

    /// <summary>На приёмке и после: сделали или нашли существующее.</summary>
    public RequestResolution? Resolution { get; set; }

    /// <summary>С какого момента на приёмке — от него считается автоприёмка.</summary>
    public DateTimeOffset? ReviewSince { get; set; }

    /// <summary>Заявке вида «исправить» — семейство с проблемой.</summary>
    public Guid? TargetCardId { get; set; }

    /// <summary>Причина отклонения.</summary>
    public string? Reason { get; set; }

    /// <summary>Сколько раз автор вернул работу («Не то»).</summary>
    public int DeclineCount { get; set; }

    /// <summary>Напоминание о скорой автоприёмке уже ушло (сбрасывается при новой приёмке).</summary>
    public bool ReminderSent { get; set; }

    /// <summary>Уведомление о просрочке уже ушло (сбрасывается при смене срока).</summary>
    public bool OverdueNotified { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>Последнее изменение: состояние, исполнитель, сообщение, вложение. По нему участник видит «есть новое».</summary>
    public DateTimeOffset UpdatedAt { get; set; }

    public DateTimeOffset? ClosedAt { get; set; }

    /// <summary>Заголовок, текст, раздел и проект в нижнем регистре — для поиска похожих.</summary>
    public string SearchText { get; set; } = "";

    public bool IsOpen => State is RequestState.New or RequestState.InWork or RequestState.NeedInfo or RequestState.Review;

    /// <summary>Открытые состояния — для запросов к базе.</summary>
    public static readonly RequestState[] OpenStates = [RequestState.New, RequestState.InWork, RequestState.NeedInfo, RequestState.Review];
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

    /// <summary>null — строка о событии без автора (закрытие публикацией по обходу, автоприёмка).</summary>
    public Guid? AuthorUserId { get; set; }

    public string Text { get; set; } = "";

    /// <summary>Строка о событии, а не сообщение человека.</summary>
    public bool IsEvent { get; set; }

    public DateTimeOffset At { get; set; }
}

/// <summary>
///     Вложение заявки: каталог производителя, чертёж, скриншот. Пока не привязано к заявке (<see cref="RequestId" /> null) — «ничейная»
///     загрузка из формы подачи или композера: файл в <c>requests/_uploads/{id}</c>; привязанное — в <c>requests/{заявка}/{id}</c>.
/// </summary>
public sealed class FamilyRequestFile
{
    public Guid Id { get; set; }

    public Guid? RequestId { get; set; }

    /// <summary>Сообщение, к которому приложен файл; null — к самой заявке.</summary>
    public Guid? MessageId { get; set; }

    public string FileName { get; set; } = "";

    public string ContentType { get; set; } = "";

    public long Size { get; set; }

    public Guid UploadedBy { get; set; }

    public DateTimeOffset At { get; set; }
}
