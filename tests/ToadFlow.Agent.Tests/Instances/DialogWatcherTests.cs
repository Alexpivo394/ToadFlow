using System.Runtime.InteropServices;
using System.Windows.Automation;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Shouldly;
using ToadFlow.Agent.Instances;
using ToadFlow.Agent.Tests.Infrastructure;

namespace ToadFlow.Agent.Tests.Instances;

public class DialogWatcherTests
{
    private readonly FakeTimeProvider _time = new(DateTimeOffset.UtcNow);
    private readonly FakeWindows _windows = new();
    private readonly DialogWatcher _watcher;

    public DialogWatcherTests() => _watcher = new DialogWatcher(_windows, 2022, 4242, _time, NullLogger.Instance);

    private FakeWindow UnsignedToadFlow(bool closes = true) =>
        _windows.Show("Безопасность — Надстройка без подписи", [@"Расположение: C:\ProgramData\Autodesk\Revit\Addins\2022\ToadFlow.Revit.Addin\ToadFlow.Revit.Addin.dll"],
            ["Всегда загружать", "Загрузить один раз", "Не загружать"], closes);

    [Test]
    public void UnsignedToadFlowAddin_AnsweredAlwaysLoad_AndClosed()
    {
        var window = UnsignedToadFlow();

        var events = _watcher.Check();

        events.ShouldHaveSingleItem().Outcome.ShouldBe(DialogOutcome.Closed);
        _windows.Answers.ShouldHaveSingleItem().Action.ShouldBe(DialogAction.AlwaysLoad);
        window.Closed.Task.IsCompleted.ShouldBeTrue();
        _watcher.Open.ShouldBeEmpty();
    }

    [Test]
    public void WindowAppearsLate_AnsweredWhenItAppears()
    {
        _watcher.Check().ShouldBeEmpty();
        _time.Advance(TimeSpan.FromMinutes(2));
        UnsignedToadFlow();

        _watcher.Check().ShouldHaveSingleItem().Outcome.ShouldBe(DialogOutcome.Closed);
    }

    [Test]
    public void AnswerDoesNotClose_RetriedLimited_ThenWaitsForPerson()
    {
        UnsignedToadFlow(closes: false);

        var outcomes = Enumerable.Range(0, DialogWatcher.MaxAttempts + 3).SelectMany(_ => _watcher.Check()).Select(e => e.Outcome).ToList();

        _windows.Answers.Count.ShouldBe(DialogWatcher.MaxAttempts);
        outcomes.Count(o => o == DialogOutcome.NotClosed).ShouldBe(DialogWatcher.MaxAttempts);
        outcomes.ShouldContain(DialogOutcome.NeedsPerson);
        _watcher.Blocking.ShouldBeNull();

        _time.Advance(DialogWatcher.AnswerGrace);
        _watcher.Blocking.ShouldNotBeNull().Title.ShouldBe("Безопасность — Надстройка без подписи");
    }

    [Test]
    public void UnknownQuestion_NeverAnswered_BlocksAfterGrace()
    {
        _windows.Show("Autodesk Revit 2022", ["Сохранить изменения?"], ["Да", "Нет", "Отмена"]);

        _watcher.Check().ShouldHaveSingleItem().Outcome.ShouldBe(DialogOutcome.NeedsPerson);
        _time.Advance(TimeSpan.FromSeconds(30));
        _watcher.Check().ShouldBeEmpty(); // о том же окне — один раз
        _watcher.Blocking.ShouldBeNull();
        _time.Advance(TimeSpan.FromSeconds(30));

        _watcher.Blocking.ShouldNotBeNull();
        _windows.Answers.ShouldBeEmpty();
    }

    [Test]
    public void WindowClosedBySomeoneElse_StopsBlocking()
    {
        var window = _windows.Show("Autodesk Revit 2022", ["Сохранить изменения?"], ["Да", "Нет"]);
        _watcher.Check();
        _time.Advance(TimeSpan.FromMinutes(2));

        _windows.Remove(window);
        _watcher.Check();

        _watcher.Blocking.ShouldBeNull();
        _watcher.Open.ShouldBeEmpty();
    }

    [Test]
    public void FatalError_ReportedOnce()
    {
        _windows.Show("Autodesk Revit 2022", ["В Revit произошла серьёзная ошибка."], ["Сохранить", "Закрыть"]);

        _watcher.Check().ShouldHaveSingleItem().Outcome.ShouldBe(DialogOutcome.Fatal);
        _watcher.Check().ShouldBeEmpty();
        _windows.Answers.ShouldBeEmpty();
    }

    [Test]
    public void Progress_Ignored()
    {
        _windows.Show("Экспорт", ["Экспорт NWC"], ["Отмена"], hasProgress: true);
        _time.Advance(TimeSpan.FromMinutes(10));

        _watcher.Check().ShouldBeEmpty();
        _watcher.Blocking.ShouldBeNull();
        _windows.Answers.ShouldBeEmpty();
    }

    [Test]
    public void SeveralWindowsAtOnce_EachHandled()
    {
        UnsignedToadFlow();
        _windows.Show("Revit", ["Аппаратное ускорение недоступно."], ["ОК", "Подробнее"]);
        _windows.Show("Безопасность — Надстройка без подписи", [@"Расположение: C:\Addins\Vendor.dll"], ["Всегда загружать", "Загрузить один раз", "Не загружать"]);

        var events = _watcher.Check();

        events.Count(e => e.Outcome == DialogOutcome.Closed).ShouldBe(2);
        events.ShouldContain(e => e.Outcome == DialogOutcome.NeedsPerson && e.Kind == DialogKind.UnsignedAddin);
        _windows.Answers.Select(a => a.Action).ShouldBe([DialogAction.AlwaysLoad, DialogAction.Dismiss]);
    }

    [Test]
    [MethodDataSource(nameof(AutomationFailures))]
    public void AutomationFailure_DoesNotStopWatching(Exception failure)
    {
        _windows.Failure = () => failure;
        Should.NotThrow(() => _watcher.Check()).ShouldBeEmpty();

        _windows.Failure = null;
        UnsignedToadFlow();

        _watcher.Check().ShouldHaveSingleItem().Outcome.ShouldBe(DialogOutcome.Closed);
    }

    public static IEnumerable<Func<Exception>> AutomationFailures() =>
    [
        () => new TimeoutException("UIA_E_TIMEOUT"),
        () => new ElementNotAvailableException(),
        () => new COMException("RPC_E_DISCONNECTED"),
        () => new UnauthorizedAccessException(),
        () => new System.ComponentModel.Win32Exception(5)
    ];
}
