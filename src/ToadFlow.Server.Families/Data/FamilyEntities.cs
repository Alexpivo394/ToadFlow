namespace ToadFlow.Server.Families.Data;

/// <summary>Состояние карточки — по тому, где лежит её файл.</summary>
public enum CardState
{
    /// <summary>Файл в библиотеке вне папки проверки.</summary>
    Approved,

    /// <summary>Файл в папке проверки.</summary>
    InReview,

    /// <summary>Файл убран из библиотеки; содержимое осталось в истории.</summary>
    Archived
}

public enum VersionState
{
    Approved,

    /// <summary>Новое семейство в папке проверки или версия-кандидат утверждённой карточки.</summary>
    InReview,

    /// <summary>Кандидат убрали из папки проверки, не утвердив.</summary>
    Rejected
}

/// <summary>Прочитала ли ферма содержимое версии.</summary>
public enum ReadState
{
    Pending,
    Done,
    Failed
}

/// <summary>Вид случая в «Нужно решение».</summary>
public enum IssueKind
{
    /// <summary>Имя файла занято другой карточкой, содержимое другое.</summary>
    NameConflict,

    /// <summary>Содержимое как у другой карточки, чей файл на месте.</summary>
    Duplicate,

    /// <summary>Файл карточки исчез, появился другой с новым содержимым: перенос с правкой или два разных события.</summary>
    Ambiguous,

    /// <summary>Ферма не смогла прочитать файл.</summary>
    Unreadable,

    /// <summary>Утверждённый файл перезаписан на месте, мимо папки проверки.</summary>
    UnreviewedChange
}

/// <summary>Семейство библиотеки: одна карточка на семейство, с историей версий. Файл лежит в сетевой папке, в базе — путь и хеш.</summary>
public sealed class FamilyCard
{
    public Guid Id { get; set; }

    /// <summary>Имя файла без расширения — имя семейства в проекте Revit. Уникально среди не архивных карточек без учёта регистра.</summary>
    public string Name { get; private set; } = "";

    /// <summary>Имя в нижнем регистре (инвариантно): по нему уникальный индекс, независимо от локали базы.</summary>
    public string NameKey { get; private set; } = "";

    /// <summary>Путь от корня библиотеки с разделителем <c>\</c>.</summary>
    public string RelativePath { get; set; } = "";

    /// <summary>Первая папка под корнем (в папке проверки — первая внутри неё); пусто — файл прямо в корне.</summary>
    public string Section { get; set; } = "";

    public CardState State { get; set; }

    /// <summary>Действующая версия: её получает проектировщик.</summary>
    public Guid? CurrentVersionId { get; set; }

    public string? Description { get; set; }

    public string[] Tags { get; set; } = [];

    public string? Manufacturer { get; set; }

    /// <summary>Из действующей версии; пусто — ферма ещё не прочитала.</summary>
    public string Category { get; set; } = "";

    /// <summary>Из действующей версии: имя значения <c>FamilyPlacementType</c>; пусто — ферма ещё не прочитала.</summary>
    public string Placement { get; set; } = "";

    public int? RevitYear { get; set; }

    public int TypeCount { get; set; }

    /// <summary>Всё, по чему ищут, в нижнем регистре через пробел.</summary>
    public string SearchText { get; set; } = "";

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    public DateTimeOffset? ArchivedAt { get; set; }

    public void SetName(string name)
    {
        Name = name;
        NameKey = name.ToLowerInvariant();
    }
}

/// <summary>Одно содержимое карточки: файл семейства с этим хешем (и каталог типоразмеров), копия — в <c>.toadflow\history</c>.</summary>
public sealed class FamilyVersion
{
    public Guid Id { get; set; }

    public Guid CardId { get; set; }

    public string Hash { get; set; } = "";

    public string? CatalogHash { get; set; }

    public long Size { get; set; }

    /// <summary>Версия Revit по файлу, прочитанная агентом без Revit.</summary>
    public int? RevitYear { get; set; }

    public VersionState State { get; set; }

    /// <summary>Где файл лежал, когда версию увидели.</summary>
    public string RelativePath { get; set; } = "";

    /// <summary>«изменён каталог типоразмеров», «изменено без проверки» или пусто.</summary>
    public string Note { get; set; } = "";

    /// <summary>Заполняется с этапа 2 (публикация из плагина).</summary>
    public Guid? AuthorUserId { get; set; }

    /// <summary>Заполняется с этапа 2 (публикация из плагина).</summary>
    public string? Comment { get; set; }

    public ReadState ReadState { get; set; }

    public string? ReadError { get; set; }

    /// <summary><c>FamilyContent</c> в JSON, когда ферма прочитала файл.</summary>
    public string? ContentJson { get; set; }

    public bool HasPreview { get; set; }

    /// <summary>Последнее задание чтения этой версии; по нему сервер видит, ждёт ли версия уже ферму.</summary>
    public Guid? ReadJobId { get; set; }

    public DateTimeOffset SeenAt { get; set; }
}

/// <summary>Случай в «Нужно решение». Закрывается сам, когда его причина исчезла из отчёта обхода.</summary>
public sealed class FamilyIssue
{
    public Guid Id { get; set; }

    public IssueKind Kind { get; set; }

    /// <summary>С какой карточкой связан.</summary>
    public Guid? CardId { get; set; }

    public string RelativePath { get; set; } = "";

    public string? Hash { get; set; }

    /// <summary>Что случилось и что сделать в проводнике.</summary>
    public string Text { get; set; } = "";

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset? ResolvedAt { get; set; }
}

/// <summary>Обход библиотеки: запрошен у агента; завершён, когда пришёл отчёт.</summary>
public sealed class FamilyScan
{
    public Guid Id { get; set; }

    public Guid? AgentId { get; set; }

    public DateTimeOffset StartedAt { get; set; }

    public DateTimeOffset? FinishedAt { get; set; }

    public bool? Complete { get; set; }

    /// <summary>Сколько файлов было в отчёте.</summary>
    public int Files { get; set; }

    public string[] Errors { get; set; } = [];
}
