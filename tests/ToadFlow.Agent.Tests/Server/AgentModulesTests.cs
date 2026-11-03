using Shouldly;
using ToadFlow.Agent.Server;
using ToadFlow.Contracts;

namespace ToadFlow.Agent.Tests.Server;

public class AgentModulesTests
{
    [Test]
    [Arguments("2.0.0", "2.0.0.0", false)]
    [Arguments("2.0.0", "1.8.0.0", true)]
    [Arguments("2.0.1-beta.3", "2.0.1.0", false)]
    [Arguments("1.8.0", "test", false)] // тесты и сборки разработчика
    [Arguments("dev", "2.0.0.0", false)]
    public void AddinOutdated_ComparesThreeNumbers(string agent, string addin, bool outdated) =>
        AgentModules.AddinOutdated(agent, addin).ShouldBe(outdated);

    [Test]
    public void OutdatedAddin_NoClashExtract()
    {
        AgentModules.For(false).ShouldContain(ModuleKinds.ClashExtract);
        AgentModules.For(true).ShouldNotContain(ModuleKinds.ClashExtract);
        AgentModules.For(true).ShouldContain(ModuleKinds.Loi);
    }
}
