using Shouldly;
using ToadFlow.Revit.Addin.Logic;

namespace ToadFlow.Revit.Addin.Tests;

public class PipeLossTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 3, 2, 0, 0, TimeSpan.Zero);

    [Test]
    public void PipeLost_AgentNotBackIn5Min_RevitExits()
    {
        var loss = new PipeLoss();

        loss.ShouldExit(T0).ShouldBeFalse(); // связь есть
        loss.Lost(T0);
        loss.Lost(T0.AddMinutes(4)); // повторная неудача отсчёт не сдвигает
        loss.ShouldExit(T0.AddMinutes(4.9)).ShouldBeFalse();
        loss.ShouldExit(T0.AddMinutes(5)).ShouldBeTrue();
    }

    [Test]
    public void PipeRestored_CountdownStartsOver()
    {
        var loss = new PipeLoss();
        loss.Lost(T0);

        loss.Restored();
        loss.ShouldExit(T0.AddMinutes(10)).ShouldBeFalse();
        loss.Lost(T0.AddMinutes(10));
        loss.ShouldExit(T0.AddMinutes(14)).ShouldBeFalse();
    }
}
