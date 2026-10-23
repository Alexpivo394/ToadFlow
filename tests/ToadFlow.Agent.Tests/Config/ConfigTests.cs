using System.Text;
using Shouldly;
using ToadFlow.Agent.Config;
using ToadFlow.Server.Tests.Infrastructure;

namespace ToadFlow.Agent.Tests.Config;

[NotInParallel(nameof(AgentPaths))]
public class AgentConfigStoreTests
{
    [Test]
    public void ConfigStore_RoundTrip_KeyEncrypted()
    {
        using var root = new TempRoot();
        var config = new AgentConfig("http://toadflow:8080", Guid.NewGuid(), "super-secret-agent-key");

        AgentConfigStore.Save(config);

        AgentConfigStore.Load().ShouldBe(config);
        var onDisk = File.ReadAllText(Path.Combine(AgentPaths.Root, "agent.json"), Encoding.UTF8);
        onDisk.ShouldNotContain("super-secret-agent-key");
        onDisk.ShouldContain("http://toadflow:8080");
    }

    [Test]
    public void ConfigStore_Missing_ReturnsNull()
    {
        using var root = new TempRoot();

        AgentConfigStore.Load().ShouldBeNull();
    }

    [Test]
    public void ConfigStore_Corrupted_ReturnsNull()
    {
        using var root = new TempRoot();
        Directory.CreateDirectory(AgentPaths.Root);
        File.WriteAllText(Path.Combine(AgentPaths.Root, "agent.json"), "{ not json");

        AgentConfigStore.Load().ShouldBeNull();
    }

    internal sealed class TempRoot : IDisposable
    {
        private readonly string _path = Path.Combine(Path.GetTempPath(), "toadflow-agent-" + Guid.NewGuid().ToString("N"));

        public TempRoot() => AgentPaths.Override = _path;

        public void Dispose()
        {
            AgentPaths.Override = null;
            if (Directory.Exists(_path)) Directory.Delete(_path, true);
        }
    }
}

public class RegistrationTests : ServerTest
{
    private sealed record CodeBody(string Code);

    [Test]
    public async Task Registration_ValidCode_ReturnsConfig()
    {
        var admin = await AdminAsync();
        var code = await (await admin.PostAsync("/api/agents/registration-codes", null)).ReadJsonAsync<CodeBody>();
        var registration = new Registration(Server.Server.CreateHandler());

        var config = await registration.RegisterAsync("http://localhost", code.Code.ToLowerInvariant(), "BIM-FARM-01");

        config.ServerUrl.ShouldBe("http://localhost");
        config.Key.ShouldNotBeNullOrWhiteSpace();
        (await admin.GetStringAsync("/api/agents")).ShouldContain("BIM-FARM-01");
    }

    [Test]
    public async Task Registration_BadCode_ThrowsWithServerMessage()
    {
        var registration = new Registration(Server.Server.CreateHandler());

        var error = await Should.ThrowAsync<AgentRegistrationException>(() => registration.RegisterAsync("http://localhost/", "TF-AAAA-BBBB", "X"));

        error.Message.ShouldContain("Код недействителен");
    }

    [Test]
    public async Task Registration_ServerUnreachable_ThrowsReadableMessage()
    {
        var registration = new Registration();

        var error = await Should.ThrowAsync<AgentRegistrationException>(() => registration.RegisterAsync("http://127.0.0.1:1", "TF-AAAA-BBBB", "X"));

        error.Message.ShouldContain("Не удалось связаться с сервером");
    }
}
