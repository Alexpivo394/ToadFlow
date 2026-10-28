using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Automation;

namespace ToadFlow.Agent.Instances;

/// <summary>
///     Окна-диалоги процесса Revit через Win32 и UI Automation. Окна ищутся перечислением окон верхнего уровня по PID
///     (<c>EnumWindows</c>): так диалог виден, даже когда главное окно Revit ещё не показано. UI Automation только читает
///     найденное окно и нажимает его кнопку. Ни ввод с клавиатуры, ни фокус, ни активное окно не нужны: всё — сообщениями окну.
/// </summary>
internal sealed partial class RevitWindows(int pid) : IRevitWindows
{
    private const string DialogClass = "#32770";
    private const string TaskDialogContent = "DirectUIHWND";
    private const int WmClose = 0x0010;
    private const int TdmClickButton = 0x0400 + 102;
    private const int IdOk = 1;
    private const uint SmtoAbortIfHung = 0x0002;
    private const uint SendTimeoutMs = 5000;

    /// <summary>ID кнопки «Всегда загружать» в TaskDialog_Security_Unsigned_File_Loading (журнал Revit; первая командная ссылка TaskDialog Revit).</summary>
    private const int AlwaysLoadButton = 1001;

    private static readonly TimeSpan CloseWait = TimeSpan.FromSeconds(2);

    public IReadOnlyList<nint> Dialogs()
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

    public WindowSnapshot Read(nint window)
    {
        var title = Title(window);
        try
        {
            var element = AutomationElement.FromHandle(window);
            var progress = element.FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.ProgressBar)).Count > 0;
            var texts = Names(Descendants(element, ControlType.Text)).Where(text => text != title).Distinct().ToList();
            var buttons = Names(Buttons(element)).ToList();
            return new WindowSnapshot(window, title, texts, buttons, progress);
        }
        catch (Exception exception) when (exception is ElementNotAvailableException or COMException or InvalidOperationException or TimeoutException
                                              or UnauthorizedAccessException or ArgumentException)
        {
            return new WindowSnapshot(window, title, [], [], false, false);
        }
    }

    public bool Close(WindowSnapshot window, DialogAction action)
    {
        var handle = window.Handle;
        if (action == DialogAction.AlwaysLoad)
        {
            Send(handle, TdmClickButton, AlwaysLoadButton);
            return WaitClosed(handle);
        }

        PostMessage(handle, WmClose, 0, 0);
        if (WaitClosed(handle)) return true;

        // TaskDialog без «Отмены» крестик игнорирует: тогда его единственная кнопка или «ОК».
        if (SingleButton(handle) is { } button)
        {
            button.Invoke();
            if (WaitClosed(handle)) return true;
        }

        if (FindWindowEx(handle, 0, TaskDialogContent, null) == 0) return false;
        Send(handle, TdmClickButton, IdOk);
        return WaitClosed(handle);
    }

    private static IEnumerable<AutomationElement> Descendants(AutomationElement element, ControlType type) =>
        element.FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, type)).Cast<AutomationElement>();

    /// <summary>Кнопки окна без кнопок его заголовка («Закрыть»): UI Automation отдаёт их среди потомков окна.</summary>
    private static List<AutomationElement> Buttons(AutomationElement window) =>
        Descendants(window, ControlType.Button)
            .Where(button => TreeWalker.ControlViewWalker.GetParent(button) is not { } parent || parent.Current.ControlType != ControlType.TitleBar)
            .ToList();

    private static IEnumerable<string> Names(IEnumerable<AutomationElement> elements) =>
        elements.Select(element => element.Current.Name?.Trim()).OfType<string>().Where(name => name.Length > 0);

    private static InvokePattern? SingleButton(nint window)
    {
        try
        {
            var buttons = Buttons(AutomationElement.FromHandle(window));
            return buttons is [var button] && button.TryGetCurrentPattern(InvokePattern.Pattern, out var pattern) ? (InvokePattern)pattern : null;
        }
        catch (Exception exception) when (exception is ElementNotAvailableException or COMException or InvalidOperationException or TimeoutException)
        {
            return null;
        }
    }

    /// <summary>Не дольше <see cref="SendTimeoutMs" />: зависший поток Revit не держит агента.</summary>
    private static void Send(nint window, int message, nint wParam) => SendMessageTimeout(window, message, wParam, 0, SmtoAbortIfHung, SendTimeoutMs, out _);

    private static bool WaitClosed(nint window)
    {
        var until = DateTime.UtcNow + CloseWait;
        while (IsWindow(window) && IsWindowVisible(window) && DateTime.UtcNow < until) Thread.Sleep(100);
        return !IsWindow(window) || !IsWindowVisible(window);
    }

    private static string ClassName(nint window)
    {
        var name = new StringBuilder(64);
        return GetClassName(window, name, name.Capacity) > 0 ? name.ToString() : "";
    }

    private static string Title(nint window)
    {
        var title = new StringBuilder(256);
        return GetWindowText(window, title, title.Capacity) > 0 ? title.ToString().Trim() : "";
    }

    private delegate bool EnumWindowsProc(nint window, nint parameter);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumWindows(EnumWindowsProc callback, nint parameter);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(nint window, StringBuilder name, int capacity);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(nint window, StringBuilder text, int capacity);

    [LibraryImport("user32.dll", EntryPoint = "FindWindowExW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial nint FindWindowEx(nint parent, nint childAfter, string? className, string? windowName);

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

    [LibraryImport("user32.dll", EntryPoint = "SendMessageTimeoutW")]
    private static partial nint SendMessageTimeout(nint window, int message, nint wParam, nint lParam, uint flags, uint timeoutMs, out nint result);
}
