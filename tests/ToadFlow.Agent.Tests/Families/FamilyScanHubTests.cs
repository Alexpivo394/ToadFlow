using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using ToadFlow.Agent.Tests.Infrastructure;
using ToadFlow.Agent.Tests.Integration;
using ToadFlow.Contracts;
using ToadFlow.Contracts.Families;
using ToadFlow.Server.Agents;
using ToadFlow.Server.Tests.Infrastructure;

namespace ToadFlow.Agent.Tests.Families;

/// <summary>Настоящий агент на связи с сервером: сообщает о семействах и начинает обход по вызову сервера. Данные агента — во временной папке.</summary>
[NotInParallel(nameof(AgentPaths))]
public class FamilyScanHubTests : ServerTest
{
    private readonly FakeLauncher _launcher = new();
    private readonly Config.AgentConfigStoreTests.TempRoot _root = new();

    [After(Test)]
    public void Cleanup() => _root.Dispose();

    [Test]
    public async Task Hello_ModulesReachServer_AndServerStartsScan()
    {
        var admin = await AdminAsync();
        await using var agent = await AgentHarness.StartAsync(Server, admin, _launcher);
        var registry = Server.Services.GetRequiredService<AgentRegistry>();
        await EventuallyAsync(() => Task.FromResult(agent.Connection.IsConnected)); // hello принят

        var modules = await DbAsync(db => db.Agents.Where(a => a.Id == agent.Config.AgentId).Select(a => a.Modules).SingleAsync());
        modules.ShouldNotBeNull().ShouldContain(ModuleKinds.FamiliesScan);
        modules.ShouldContain(ModuleKinds.FamiliesRead);

        var started = await registry.Client(agent.Config.AgentId)!.StartFamilyScan(
            new FamilyScanRequestDto(Guid.NewGuid(), Path.Combine(Path.GetTempPath(), "нет-библиотеки-" + Guid.NewGuid().ToString("N")), "_На проверке"));

        started.ShouldBeTrue();
    }

    [Test]
    public async Task FileAction_ServerCallsAgent_AgentAnswers()
    {
        var admin = await AdminAsync();
        await using var agent = await AgentHarness.StartAsync(Server, admin, _launcher);
        var registry = Server.Services.GetRequiredService<AgentRegistry>();
        await EventuallyAsync(() => Task.FromResult(agent.Connection.IsConnected));

        var modules = await DbAsync(db => db.Agents.Where(a => a.Id == agent.Config.AgentId).Select(a => a.Modules).SingleAsync());
        modules.ShouldNotBeNull().ShouldContain(ModuleKinds.FamiliesFiles);
        var result = await registry.Client(agent.Config.AgentId)!.FamilyFileAction(new FamilyFileActionDto(FamilyFileActionKind.Delete,
            Path.Combine(Path.GetTempPath(), "нет-библиотеки-" + Guid.NewGuid().ToString("N")), @"ОВ\Клапан.rfa", null, new string('a', 64), null, null));

        result.Error.ShouldNotBeNull().ShouldContain("не видит");
    }
}
