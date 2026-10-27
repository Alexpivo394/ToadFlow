using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Automation;

namespace ToadFlow.Agent.Instances;

/// <summary>
///     Окна-сообщения, которыми Revit или его экспортер остановил поток Revit API, например «Не найдена подходящая геометрия»
///     от Navisworks Exporter. На машине с агентом нажать их некому: Revit ждал бы до таймаута задания. Агент закрывает такое
///     окно, как пользователь крестиком или «ОК», и возвращает его текст для журнала задания.
///     <para>
///         Сообщение — стандартный диалог Windows (<c>#32770</c>: MessageBox и TaskDialog). Окна с полосой хода не трогаются:
///         это сама долгая операция, а не вопрос.
///     </para>
/// </summary>
internal static partial class RevitMessageDialogs
{
    private const string DialogClass = "#32770";
    private const int WmClose = 0x0010;

    public static IReadOnlyList<string> Close(int pid)
    {
        var closed = new List<string>();
        foreach (var window in DialogWindows(pid))
        {
            try
            {
                var element = AutomationElement.FromHandle(window);
                var progressBars = element.FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.ProgressBar));
                if (progressBars.Count > 0) continue;

                var description = Describe(element);
                PostMessage(window, WmClose, 0, 0);
                // TaskDialog без «Отмены» крестик игнорирует — тогда нажимаем его единственную кнопку.
                if (!WaitClosed(window) && SingleButton(element) is { } button)
                {
                    button.Invoke();
                    WaitClosed(window);
                }

                closed.Add(IsWindow(window) ? $"{description} (закрыть не удалось)" : description);
            }
            catch (Exception exception) when (exception is ElementNotAvailableException or COMException or InvalidOperationException)
            {
                // Окно закрылось само, пока его читали.
            }
        }

        return closed;
    }

    private static List<nint> DialogWindows(int pid)
    {
        var found = new List<nint>();
        EnumWindows((window, _) =>
        {
            if (IsWindowVisible(window) && GetWindowThreadProcessId(window, out var owner) != 0 && owner == (uint)pid && ClassName(window) == DialogClass)
                found.Add(window);
            return true;
        }, 0);
        return found;
    }

    private static string Describe(AutomationElement window)
    {
        var lines = window.FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Text))
            .Cast<AutomationElement>()
            .Select(text => text.Current.Name?.Trim())
            .Where(text => !string.IsNullOrEmpty(text))
            .Distinct();
        var title = window.Current.Name?.Trim();
        var body = string.Join(" ", lines);
        return $"«{title}»" + (body.Length == 0 ? "" : $": {body}");
    }

    private static InvokePattern? SingleButton(AutomationElement window)
    {
        var buttons = window.FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button));
        return buttons.Count == 1 && buttons[0].TryGetCurrentPattern(InvokePattern.Pattern, out var pattern) ? (InvokePattern)pattern : null;
    }

    private static bool WaitClosed(nint window)
    {
        for (var attempt = 0; attempt < 10 && IsWindow(window); attempt++) Thread.Sleep(100);
        return !IsWindow(window);
    }

    private static string ClassName(nint window)
    {
        var name = new StringBuilder(64);
        return GetClassName(window, name, name.Capacity) > 0 ? name.ToString() : "";
    }

    private delegate bool EnumWindowsProc(nint window, nint parameter);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumWindows(EnumWindowsProc callback, nint parameter);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(nint window, StringBuilder name, int capacity);

    [LibraryImport("user32.dll")]
    private static partial uint GetWindowThreadProcessId(nint window, out uint processId);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool IsWindowVisible(nint window);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool IsWindow(nint window);

    [LibraryImport("user32.dll", EntryPoint = "PostMessageW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool PostMessage(nint window, int message, nint wParam, nint lParam);
}
