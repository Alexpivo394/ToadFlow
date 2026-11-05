using Alexpivo394.Revit.Export.Core;
using Shouldly;
using ToadFlow.Contracts;
using ToadFlow.Revit.Addin.Logic;

namespace ToadFlow.Revit.Addin.Tests;

public class EmptyViewTests
{
    [Test]
    [Arguments(0, 511_502, true)]
    [Arguments(0, 0, false)]
    [Arguments(50_114, 511_502, false)]
    public void IsEmpty_NoVisibleElementsWhileModelHasThem(int elements, int inventory, bool expected) =>
        EmptyView.IsEmpty(elements, inventory).ShouldBe(expected);

    [Test]
    public void RetryNwc_MissingNwc_RetriedInSameRevit() =>
        EmptyView.RetryNwc(new ModelResult { Rvt = StepResult.Done, Nwc = StepResult.Skipped, NwcMissing = true }).ShouldBeTrue();

    [Test]
    public void RetryNwc_RevitFailure_GoesToFreshRevit() =>
        EmptyView.RetryNwc(new ModelResult { NwcMissing = true, RevitFailure = true }).ShouldBeFalse();

    [Test]
    public void RetryNwc_SkippedByUser_NotRetried() =>
        EmptyView.RetryNwc(new ModelResult { NwcMissing = true, SkippedByUser = true }).ShouldBeFalse();

    [Test]
    public void RetryNwc_NwcDone_NotRetried() =>
        EmptyView.RetryNwc(new ModelResult { Rvt = StepResult.Done, Nwc = StepResult.Done }).ShouldBeFalse();

    [Test]
    public void ClashOutcome_AttemptsLeft_RetriedInFreshRevit()
    {
        var (outcome, note) = EmptyView.ClashOutcome(canRetry: true, "Navisworks", 511_502);

        outcome.ShouldBe(JobOutcome.InfrastructureFailure);
        note.ShouldBe("на виде «Navisworks» нет видимых элементов, хотя в модели их 511 502 — модель повторится в новом Revit");
    }

    [Test]
    public void ClashOutcome_LastAttempt_FailsInsteadOfEmptySnapshot()
    {
        var (outcome, note) = EmptyView.ClashOutcome(canRetry: false, "Navisworks", 511_502);

        outcome.ShouldBe(JobOutcome.Failed);
        note.ShouldBe("на виде «Navisworks» нет видимых элементов, хотя в модели их 511 502");
    }

    [Test]
    public void Describe_Workshared()
    {
        var facts = new ViewFacts("Navisworks", "Navisworks", "Монолит", SectionBox: false, TemporaryHide: false, Worksets: 27, OpenWorksets: 27,
            VisibleWorksets: 15, ModelElements: 511_502, ModelElementsInView: 0);

        EmptyView.Describe(facts).ShouldBe(
            "вид «Navisworks»: элементов модели на виде 0 из 511 502; рабочие наборы: открыто 27 из 27, видно на виде 15; " +
            "шаблон «Navisworks», стадия «Монолит», 3D-подрезка выкл., временное скрытие выкл.");
    }

    [Test]
    public void Describe_NotWorkshared_NoTemplate()
    {
        var facts = new ViewFacts("3D", null, null, SectionBox: true, TemporaryHide: true, Worksets: 0, OpenWorksets: 0, VisibleWorksets: 0,
            ModelElements: 10, ModelElementsInView: 2);

        EmptyView.Describe(facts).ShouldBe(
            "вид «3D»: элементов модели на виде 2 из 10; без рабочих наборов; без шаблона, стадия не задана, 3D-подрезка вкл., временное скрытие вкл.");
    }

    [Test]
    public void ReadyGate_OpensOnce()
    {
        var gate = new ReadyGate();

        gate.TryOpen().ShouldBeTrue();
        gate.TryOpen().ShouldBeFalse();
    }
}
