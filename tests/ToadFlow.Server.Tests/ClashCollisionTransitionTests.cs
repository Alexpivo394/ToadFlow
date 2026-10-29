using Shouldly;
using ToadFlow.Server.Clash.Collisions;
using static ToadFlow.Server.Clash.Collisions.CollisionStatus;
using static ToadFlow.Server.Clash.Collisions.ElementPresence;

namespace ToadFlow.Server.Tests;

public class ClashCollisionTransitionTests
{
    private static readonly GoneFacts Moved = new(InGeometry, InGeometry, ModelUnchecked: false, PreviousConfigWouldHit: null);

    [Test]
    [Arguments(New, Active)]
    [Arguments(Active, Active)]
    [Arguments(Returned, Active)]
    [Arguments(Unchecked, Active)]
    [Arguments(GoneByRules, Active)]
    [Arguments(Fixed, Returned)]
    [Arguments(Fake, Fake)]
    public void Found_Transitions(CollisionStatus current, CollisionStatus next) =>
        CollisionTransitions.Found(current).Next.ShouldBe(next);

    [Test]
    public void Found_FixedBecomesReturned() => CollisionTransitions.Found(Fixed).ShouldBe((Returned, (string?)null));

    [Test]
    public void Found_FixedPendingBecomesActiveWithNote() =>
        CollisionTransitions.Found(FixedPending).ShouldBe((Active, "Отмечена исправленной, но пересечение осталось"));

    [Test]
    public void Found_FakeStaysFake() => CollisionTransitions.Found(Fake).ShouldBe((Fake, (string?)null));

    [Test]
    public void Gone_ModelUnchecked_Unchecked() =>
        CollisionTransitions.NotFound(Active, Moved with { ModelUnchecked = true, A = Absent }).ShouldBe(Unchecked);

    [Test]
    public void Gone_ElementDeleted_Fixed()
    {
        CollisionTransitions.NotFound(Active, Moved with { B = Absent }).ShouldBe(Fixed);
        CollisionTransitions.NotFound(New, Moved with { A = Absent, PreviousConfigWouldHit = true }).ShouldBe(Fixed);
    }

    [Test]
    public void Gone_ElementHiddenOnView_GoneByRules() =>
        CollisionTransitions.NotFound(Returned, Moved with { A = InventoryOnly }).ShouldBe(GoneByRules);

    [Test]
    public void Gone_ConfigChanged_PreviousWouldHit_GoneByRules() =>
        CollisionTransitions.NotFound(Active, Moved with { PreviousConfigWouldHit = true }).ShouldBe(GoneByRules);

    [Test]
    public void Gone_ConfigChanged_PreviousWouldNotHit_Fixed() =>
        CollisionTransitions.NotFound(Active, Moved with { PreviousConfigWouldHit = false }).ShouldBe(Fixed);

    [Test]
    public void Gone_SameConfigMoved_Fixed() => CollisionTransitions.NotFound(FixedPending, Moved).ShouldBe(Fixed);

    [Test]
    public void Gone_FakeStaysFake()
    {
        CollisionTransitions.NotFound(Fake, Moved).ShouldBe(Fake);
        CollisionTransitions.NotFound(Fake, Moved with { ModelUnchecked = true }).ShouldBe(Fake);
    }

    [Test]
    public void Gone_FixedStaysFixed() => CollisionTransitions.NotFound(Fixed, Moved with { ModelUnchecked = true }).ShouldBe(Fixed);

    [Test]
    public void Gone_UncheckedThenChecked_Fixed() => CollisionTransitions.NotFound(Unchecked, Moved).ShouldBe(Fixed);

    [Test]
    public void Gone_GoneByRulesStays() => CollisionTransitions.NotFound(GoneByRules, Moved with { A = InventoryOnly }).ShouldBe(GoneByRules);

    [Test]
    public void Manual_FakeWithoutNote_Rejected()
    {
        Should.Throw<CollisionTransitionException>(() => CollisionTransitions.Manual(Active, Fake, foundNow: true, note: " "))
            .Message.ShouldContain("примечание");
        CollisionTransitions.Manual(Active, Fake, foundNow: true, note: "так задумано").ShouldBe(Fake);
    }

    [Test]
    public void Manual_FixedPending_OnlyFromOpen()
    {
        CollisionTransitions.Manual(New, FixedPending, true, null).ShouldBe(FixedPending);
        CollisionTransitions.Manual(Returned, FixedPending, true, null).ShouldBe(FixedPending);
        Should.Throw<CollisionTransitionException>(() => CollisionTransitions.Manual(Fixed, FixedPending, false, null));
        Should.Throw<CollisionTransitionException>(() => CollisionTransitions.Manual(Fake, FixedPending, true, null));
    }

    [Test]
    public void Manual_UnsetFake_ByFoundNow()
    {
        CollisionTransitions.Manual(Fake, Active, foundNow: true, note: null).ShouldBe(Active);
        CollisionTransitions.Manual(Fake, Active, foundNow: false, note: null).ShouldBe(Fixed);
    }

    [Test]
    public void Manual_SystemStatuses_Rejected()
    {
        Should.Throw<CollisionTransitionException>(() => CollisionTransitions.Manual(Active, Fixed, true, null));
        Should.Throw<CollisionTransitionException>(() => CollisionTransitions.Manual(Active, Active, true, null));
    }
}
