namespace ToadFlow.Server.Notifications;

/// <summary>Уведомление человеку: колокольчик в вебе и всплывашка в Revit.</summary>
public sealed class Notification
{
    public long Id { get; set; }

    public Guid UserId { get; set; }

    /// <summary>Вид — одна из <see cref="NotificationKinds" />; по нему человек отключает ненужное.</summary>
    public string Kind { get; set; } = "";

    public string Title { get; set; } = "";

    public string Text { get; set; } = "";

    /// <summary>Путь веба, куда ведёт уведомление.</summary>
    public string? Link { get; set; }

    /// <summary>Ключ склейки: непрочитанное с тем же ключом не дублируется, а растёт <see cref="Count" />.</summary>
    public string? GroupKey { get; set; }

    /// <summary>Сколько событий склеено в уведомление.</summary>
    public int Count { get; set; } = 1;

    public DateTimeOffset At { get; set; }

    public DateTimeOffset? ReadAt { get; set; }
}

/// <summary>Виды уведомлений. Названия — для настройки в профиле.</summary>
public static class NotificationKinds
{
    public const string RequestNew = "request.new";
    public const string RequestAssigned = "request.assigned";
    public const string RequestAsk = "request.ask";
    public const string RequestReview = "request.review";
    public const string RequestRemind = "request.remind";
    public const string RequestDeclined = "request.declined";
    public const string RequestMessage = "request.message";
    public const string RequestClosed = "request.closed";
    public const string RequestJoined = "request.joined";
    public const string RequestOverdue = "request.overdue";
    public const string FamilyUpdated = "family.updated";
    public const string FamilyReview = "family.review";
    public const string RunFailed = "run.failed";
    public const string ClashNew = "clash.new";
    public const string UserPending = "user.pending";

    /// <summary>Вид, группа в профиле и название — в порядке показа.</summary>
    public static readonly IReadOnlyList<(string Kind, string Group, string Title)> All =
    [
        (RequestNew, "Заявки на семейства", "Новая заявка в моём направлении"),
        (RequestAssigned, "Заявки на семейства", "Заявку передали мне"),
        (RequestAsk, "Заявки на семейства", "Координатор задал вопрос по моей заявке"),
        (RequestReview, "Заявки на семейства", "Моя заявка готова — нужно принять"),
        (RequestRemind, "Заявки на семейства", "Напоминание: заявка скоро примется сама"),
        (RequestDeclined, "Заявки на семейства", "Автор вернул работу («Не то»)"),
        (RequestMessage, "Заявки на семейства", "Новое сообщение в заявке"),
        (RequestClosed, "Заявки на семейства", "Заявка закрыта"),
        (RequestJoined, "Заявки на семейства", "К моей заявке присоединились"),
        (RequestOverdue, "Заявки на семейства", "Заявка просрочена"),
        (FamilyUpdated, "Семейства", "Обновилось семейство, которое я загружал"),
        (FamilyReview, "Семейства", "Семейство пришло на проверку"),
        (RunFailed, "Ферма", "Ошибки в расписании, за которым я слежу"),
        (ClashNew, "Коллизии", "Новые коллизии в моих разделах"),
        (UserPending, "Администрирование", "Новая заявка на регистрацию")
    ];

    public static bool IsKnown(string kind) => All.Any(k => k.Kind == kind);
}
