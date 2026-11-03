using Shouldly;
using ToadFlow.Contracts;
using ToadFlow.Contracts.Clash;
using ToadFlow.Server.Agents;

namespace ToadFlow.Server.Tests;

public class ClashContractsTests
{
    [Test]
    [Arguments("1.7.1", false)]
    [Arguments("1.1.0", false)]
    [Arguments("1.8.0", true)]
    [Arguments("2.0.0-beta.1", true)]
    [Arguments("0.1.0", true)]
    [Arguments("dev", true)]
    [Arguments("", true)]
    public void CanRun_ClashExtract_RequiresAgent18(string version, bool canRun) =>
        AgentCapabilities.CanRun(version, ModuleKinds.ClashExtract).ShouldBe(canRun);

    [Test]
    public void CanRun_Loi_StillAllowedOnAgent171() => AgentCapabilities.CanRun("1.7.1", ModuleKinds.Loi).ShouldBeTrue();

    [Test]
    public void ClashStaging_IsInside_RejectsTraversal()
    {
        var job = Guid.NewGuid();

        ClashStaging.IsInside(ClashStaging.Folder(job, 0)).ShouldBeTrue();
        ClashStaging.Folder(job, 0).ShouldEndWith(job.ToString("N"));
        ClashStaging.IsInside(Path.Combine(ClashStaging.Root, "..", "x")).ShouldBeFalse();
        ClashStaging.IsInside(ClashStaging.Root).ShouldBeFalse();
        ClashStaging.IsInside(ClashStaging.Root + "-evil" + Path.DirectorySeparatorChar + "x").ShouldBeFalse();
        ClashStaging.IsInside(@"Z:\elsewhere\x").ShouldBeFalse();
        ClashStaging.IsInside("").ShouldBeFalse();
    }

    [Test]
    public void ClashUploads_FileNames()
    {
        ClashUploads.MaxChunkBytes.ShouldBe(8 * 1024 * 1024);
        ClashUploads.FileName("geometry").ShouldBe("model.tcg");
        ClashUploads.FileName("parameters").ShouldBe("model.tcp");
        ClashUploads.FileName("other").ShouldBeNull();
    }
}
