using System.Runtime.InteropServices;
using System.Windows.Automation;
using Microsoft.Extensions.Logging;

namespace ToadFlow.Agent.Instances;

/// <summary>
///     Окно Revit «Безопасность — Надстройка без подписи» про аддин ToadFlow: на машине с агентом нажать его некому, и Revit
///     ждал бы до таймаута запуска. Агент находит окно в запущенном им Revit по пути к dll аддина (не зависит от языка Revit)
///     и нажимает «Всегда загружать». Отметка в реестре (<see cref="AddinTrust" />) Revit 2022 при запуске не учитывает.
/// </summary>
internal static partial class UnsignedAddinDialog
{
    public const string AddinDll = "ToadFlow.Revit.Addin.dll";

    private const int TdmClickButton = 0x0400 + 102;

    /// <summary>ID кнопки «Всегда загружать» в TaskDialog_Security_Unsigned_File_Loading (журнал Revit).</summary>
    private const int AlwaysLoadButton = 1001;

    private static readonly TimeSpan PollEvery = TimeSpan.FromSeconds(1);

    /// <summary>Следит за окном, пока Revit запускается: <paramref name="duration" /> или до выхода процесса.</summary>
    public static async Task WatchAsync(int pid, Task exited, TimeSpan duration, ILogger logger)
    {
        var until = DateTime.UtcNow + duration;
        while (DateTime.UtcNow < until && !exited.IsCompleted)
        {
            try
            {
                if (TryAnswer(pid)) logger.LogInformation("Revit (PID {Pid}) спросил про неподписанный аддин ToadFlow — ответ «Всегда загружать»", pid);
            }
            catch (Exception exception) when (exception is ElementNotAvailableException or COMException or InvalidOperationException)
            {
                // Окно закрылось или Revit перестраивает интерфейс — проверим на следующем шаге.
            }

            await Task.WhenAny(exited, Task.Delay(PollEvery));
        }
    }

    /// <returns>Окно было и на него ответили.</returns>
    private static bool TryAnswer(int pid)
    {
        var answered = false;
        var ofProcess = new PropertyCondition(AutomationElement.ProcessIdProperty, pid);
        var windows = new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Window);
        foreach (AutomationElement top in AutomationElement.RootElement.FindAll(TreeScope.Children, ofProcess))
        {
            // Диалог Revit — окно верхнего уровня или дочернее окно главного окна, смотря по версии.
            var candidates = new List<AutomationElement> { top };
            candidates.AddRange(top.FindAll(TreeScope.Descendants, windows).Cast<AutomationElement>());
            foreach (var window in candidates.Where(IsUnsignedAddinDialog))
            {
                SendMessage(window.Current.NativeWindowHandle, TdmClickButton, AlwaysLoadButton, 0);
                answered = true;
            }
        }

        return answered;
    }

    private static bool IsUnsignedAddinDialog(AutomationElement window) =>
        window.FindAll(TreeScope.Children, Condition.TrueCondition).Cast<AutomationElement>()
            .Any(child => child.Current.Name?.Contains(AddinDll, StringComparison.OrdinalIgnoreCase) == true);

    [LibraryImport("user32.dll", EntryPoint = "SendMessageW")]
    private static partial nint SendMessage(nint window, int message, nint wParam, nint lParam);
}
