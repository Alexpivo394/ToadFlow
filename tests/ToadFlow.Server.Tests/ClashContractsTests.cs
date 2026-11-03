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

    /// <summary>Список видов из hello решает, а не номер версии; без списка (агент до 2.0) — по версии, как раньше.</summary>
    [Test]
    public void Capabilities_ModulesListWins_VersionFallback()
    {
        // Сборка CI 1.7.2-…: по версии коллизий бы не было, но агент сказал, что умеет.
        AgentCapabilities.CanRun("1.7.2-ci.5", [ModuleKinds.ClashExtract], ModuleKinds.ClashExtract).ShouldBeTrue();
        // Агент 2.0 с устаревшим аддином убрал коллизии из списка — не получает, хотя версия свежая.
        AgentCapabilities.CanRun("2.0.0", [ModuleKinds.Loi], ModuleKinds.ClashExtract).ShouldBeFalse();
        AgentCapabilities.CanRun("2.0.0", [], ModuleKinds.Export).ShouldBeTrue(); // выгрузку умеют все
        AgentCapabilities.CanRun("1.7.1", null, ModuleKinds.ClashExtract).ShouldBeFalse();
        AgentCapabilities.CanRun("1.8.0", null, ModuleKinds.ClashExtract).ShouldBeTrue();
        AgentCapabilities.Runnable("2.0.0", [ModuleKinds.Loi]).ShouldBe([ModuleKinds.Export, ModuleKinds.Loi]);
    }

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
