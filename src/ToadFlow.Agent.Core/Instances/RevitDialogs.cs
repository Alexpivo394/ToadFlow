namespace ToadFlow.Agent.Instances;

/// <summary>Что агент прочитал в окне-диалоге Revit.</summary>
/// <param name="Texts">Надписи окна: заголовок не входит, повторы убраны.</param>
/// <param name="Buttons">Подписи кнопок, включая командные ссылки TaskDialog.</param>
/// <param name="HasProgress">В окне полоса хода — это сама долгая операция, а не вопрос.</param>
/// <param name="Readable">false — UI Automation не ответила: известен только заголовок, окно прочитается на следующем шаге.</param>
public sealed record WindowSnapshot(nint Handle, string Title, IReadOnlyList<string> Texts, IReadOnlyList<string> Buttons, bool HasProgress, bool Readable = true)
{
    private const int MaxDescription = 400;

    /// <summary>Для журналов: «заголовок»: текст — без кнопок и не длиннее нескольких строк.</summary>
    public string Describe()
    {
        var body = string.Join(" ", Texts);
        var text = $"«{Title}»" + (body.Length == 0 ? "" : $": {body}");
        return text.Length <= MaxDescription ? text : $"{text[..MaxDescription]}…";
    }
}

/// <summary>Как агент отвечает на окно, которое знает.</summary>
public enum DialogAction
{
    /// <summary>«Всегда загружать» в окне про неподписанный аддин ToadFlow.</summary>
    AlwaysLoad,

    /// <summary>Закрыть окно-сообщение: крестиком, единственной кнопкой или «ОК».</summary>
    Dismiss
}

/// <summary>Окна-диалоги одного процесса Revit: агент видит только окна этого PID.</summary>
public interface IRevitWindows
{
    /// <summary>Открытые сейчас окна-диалоги процесса (стандартные диалоги Windows: MessageBox и TaskDialog).</summary>
    IReadOnlyList<nint> Dialogs();

    WindowSnapshot Read(nint window);

    /// <returns>Окно закрылось.</returns>
    bool Close(WindowSnapshot window, DialogAction action);
}

/// <summary>Процесс без окон: тестовый Revit и процесс, окна которого агенту недоступны.</summary>
public sealed class NoRevitWindows : IRevitWindows
{
    public static readonly NoRevitWindows Instance = new();

    public IReadOnlyList<nint> Dialogs() => [];

    public WindowSnapshot Read(nint window) => new(window, "", [], [], false, false);

    public bool Close(WindowSnapshot window, DialogAction action) => false;
}

/// <summary>Что за окно показал Revit.</summary>
public enum DialogKind
{
    /// <summary>Окно, которого агент не знает: вопрос с несколькими ответами или окно, которое не удалось прочитать.</summary>
    Unknown,

    /// <summary>Окно с полосой хода или без кнопок: идёт операция, отвечать не на что.</summary>
    Progress,

    /// <summary>«Безопасность — Надстройка без подписи» про аддин ToadFlow.</summary>
    UnsignedToadFlowAddin,

    /// <summary>«Надстройка без подписи» про сторонний аддин: загружать ли чужой код, решает человек.</summary>
    UnsignedAddin,

    /// <summary>Другое окно про аддин ToadFlow — обычно ошибка его загрузки: без аддина Revit агенту бесполезен.</summary>
    ToadFlowAddinFailure,

    /// <summary>Серьёзная ошибка Revit: процесс в неизвестном состоянии.</summary>
    RevitFatalError,

    /// <summary>Аппаратное ускорение графики недоступно — Revit работает дальше без него.</summary>
    HardwareAcceleration,

    /// <summary>Путь к шаблонам семейств не найден — Revit работает дальше, шаблоны агенту не нужны.</summary>
    FamilyTemplatePath,

    /// <summary>Сообщение с единственной кнопкой: выбора нет, только «понятно».</summary>
    Message
}

/// <summary>Как агент поступает с окном этого вида.</summary>
public enum DialogResponse
{
    /// <summary>Не трогать и не ждать ответа.</summary>
    Ignore,

    /// <summary>Ответить действием <see cref="DialogAction" />.</summary>
    Answer,

    /// <summary>Не отвечать: ответ за человеком. Окно, висящее дольше <see cref="DialogWatcher.AnswerGrace" />, — сбой экземпляра.</summary>
    AskPerson,

    /// <summary>Сразу сбой экземпляра: Revit убивается.</summary>
    Fail
}

/// <summary>
///     Таблица известных окон Revit. Признаки — путь к dll аддина (не зависит от языка) и слова русского и английского
///     интерфейса. Агент отвечает только на окна, ответ на которые не меняет модель и не решает за человека; на неизвестный
///     вопрос он не отвечает никогда.
/// </summary>
public static class DialogRules
{
    public const string ToadFlowAddin = "ToadFlow.Revit.Addin";

    public static DialogKind Classify(WindowSnapshot window)
    {
        if (window.HasProgress) return DialogKind.Progress;
        if (!window.Readable) return DialogKind.Unknown;

        var text = string.Join(" ", [window.Title, .. window.Texts]).ToLowerInvariant();
        var aboutToadFlow = text.Contains(ToadFlowAddin, StringComparison.OrdinalIgnoreCase);
        var aboutSignature = Has(text, "unsigned", "signature", "подпис");
        var aboutAddin = Has(text, "add-in", "addin", "надстройк", ".dll");

        if (aboutToadFlow) return aboutSignature ? DialogKind.UnsignedToadFlowAddin : DialogKind.ToadFlowAddinFailure;
        if (aboutSignature && aboutAddin) return DialogKind.UnsignedAddin;
        if (Has(text, "serious error") || (Has(text, "серьезн", "серьёзн") && Has(text, "ошибк"))) return DialogKind.RevitFatalError;
        if (Has(text, "hardware acceleration") || (Has(text, "аппаратн") && Has(text, "ускорен"))) return DialogKind.HardwareAcceleration;
        if (Has(text, "family template") || (Has(text, "шаблон") && Has(text, "семейств"))) return DialogKind.FamilyTemplatePath;

        return window.Buttons.Count switch
        {
            0 => DialogKind.Progress,
            1 => DialogKind.Message,
            _ => DialogKind.Unknown
        };
    }

    public static (DialogResponse Response, DialogAction Action) Respond(DialogKind kind) => kind switch
    {
        DialogKind.Progress => (DialogResponse.Ignore, default),
        DialogKind.UnsignedToadFlowAddin => (DialogResponse.Answer, DialogAction.AlwaysLoad),
        DialogKind.HardwareAcceleration or DialogKind.FamilyTemplatePath or DialogKind.Message => (DialogResponse.Answer, DialogAction.Dismiss),
        DialogKind.ToadFlowAddinFailure or DialogKind.RevitFatalError => (DialogResponse.Fail, default),
        _ => (DialogResponse.AskPerson, default)
    };

    /// <summary>Для журналов: что агент понял про окно.</summary>
    public static string Describe(DialogKind kind) => kind switch
    {
        DialogKind.Progress => "ход операции",
        DialogKind.UnsignedToadFlowAddin => "неподписанный аддин ToadFlow",
        DialogKind.UnsignedAddin => "неподписанная сторонняя надстройка — загружать ли её, решает человек",
        DialogKind.ToadFlowAddinFailure => "ошибка аддина ToadFlow",
        DialogKind.RevitFatalError => "серьёзная ошибка Revit",
        DialogKind.HardwareAcceleration => "нет аппаратного ускорения графики",
        DialogKind.FamilyTemplatePath => "не найден путь к шаблонам семейств",
        DialogKind.Message => "сообщение",
        _ => "неизвестное окно — агент на такие не отвечает"
    };

    private static bool Has(string text, params ReadOnlySpan<string> words)
    {
        foreach (var word in words)
            if (text.Contains(word, StringComparison.Ordinal))
                return true;
        return false;
    }
}
