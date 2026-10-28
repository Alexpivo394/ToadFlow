using Shouldly;
using ToadFlow.Agent.Instances;

namespace ToadFlow.Agent.Tests.Instances;

/// <summary>
///     Настоящие окна Windows без Revit: тест показывает в своём процессе MessageBox и TaskDialog (comctl32 — те же, что у
///     Revit), а <see cref="RevitWindows" /> ищет их по PID, читает через UI Automation и закрывает сообщениями окну.
/// </summary>
[NotInParallel]
public class RevitWindowsTests
{
    private const string AddinLocation = @"C:\ProgramData\Autodesk\Revit\Addins\2022\ToadFlow.Revit.Addin\ToadFlow.Revit.Addin.dll";

    private readonly RevitWindows _windows = new(Environment.ProcessId);

    [Test]
    public async Task MessageBox_FoundReadAndDismissed()
    {
        var shown = ShowOnUiThread(() => MessageBox.Show("Не найдена подходящая геометрия.", "Navisworks"));

        var snapshot = _windows.Read(await AppearsAsync("Navisworks"));

        snapshot.Readable.ShouldBeTrue();
        snapshot.Texts.ShouldContain("Не найдена подходящая геометрия.");
        snapshot.Buttons.Count.ShouldBe(1);
        DialogRules.Classify(snapshot).ShouldBe(DialogKind.Message);
        _windows.Close(snapshot, DialogAction.Dismiss).ShouldBeTrue();
        await shown.WaitAsync(TimeSpan.FromSeconds(10));
    }

    [Test]
    public async Task TaskDialog_AddinPathInContent_RecognisedAsUnsignedToadFlowAddin()
    {
        var page = new TaskDialogPage
        {
            Caption = "Безопасность — Надстройка без подписи",
            Heading = "Не удалось проверить издателя этой надстройки.",
            Text = $"Имя: ToadFlow\nРасположение: {AddinLocation}",
            AllowCancel = true,
            Buttons = { new TaskDialogCommandLinkButton("Всегда загружать"), new TaskDialogCommandLinkButton("Загрузить один раз"), new TaskDialogCommandLinkButton("Не загружать") }
        };
        var shown = ShowOnUiThread(() => TaskDialog.ShowDialog(page));

        var snapshot = _windows.Read(await AppearsAsync("Безопасность — Надстройка без подписи"));

        snapshot.Readable.ShouldBeTrue();
        snapshot.Texts.ShouldContain(text => text.Contains(AddinLocation));
        snapshot.Buttons.Count.ShouldBe(3); // без кнопки «Закрыть» заголовка
        snapshot.Buttons.ShouldContain(button => button.StartsWith("Всегда загружать"));
        DialogRules.Classify(snapshot).ShouldBe(DialogKind.UnsignedToadFlowAddin);
        _windows.Close(snapshot, DialogAction.Dismiss).ShouldBeTrue();
        await shown.WaitAsync(TimeSpan.FromSeconds(10));
    }

    [Test]
    public async Task TaskDialog_SingleOk_WithoutCancel_DismissedByItsButton()
    {
        var page = new TaskDialogPage { Caption = "Autodesk Revit 2022", Text = "Аппаратное ускорение недоступно.", AllowCancel = false, Buttons = { TaskDialogButton.OK } };
        var shown = ShowOnUiThread(() => TaskDialog.ShowDialog(page));

        var snapshot = _windows.Read(await AppearsAsync("Autodesk Revit 2022"));

        snapshot.Buttons.ShouldHaveSingleItem();
        DialogRules.Classify(snapshot).ShouldBe(DialogKind.HardwareAcceleration);
        _windows.Close(snapshot, DialogAction.Dismiss).ShouldBeTrue();
        await shown.WaitAsync(TimeSpan.FromSeconds(10));
    }

    [Test]
    public void OtherProcess_WindowsNotSeen() => new RevitWindows(int.MaxValue - 1).Dialogs().ShouldBeEmpty();

    private async Task<nint> AppearsAsync(string title)
    {
        for (var attempt = 0; attempt < 100; attempt++)
        {
            foreach (var window in _windows.Dialogs())
                if (_windows.Read(window).Title == title)
                    return window;
            await Task.Delay(100);
        }

        throw new TimeoutException($"Окно «{title}» не появилось");
    }

    private static Task ShowOnUiThread(Action show)
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                Application.EnableVisualStyles();
                show();
                done.TrySetResult();
            }
            catch (Exception exception)
            {
                done.TrySetException(exception);
            }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return done.Task;
    }
}
