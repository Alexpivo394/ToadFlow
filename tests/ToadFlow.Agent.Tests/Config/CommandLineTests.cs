using Shouldly;
using ToadFlow.Agent.Config;
using ToadFlow.Server.Tests.Infrastructure;

namespace ToadFlow.Agent.Tests.Config;

[NotInParallel(nameof(AgentPaths))]
public class CommandLineTests : ServerTest
{
    private sealed record CodeBody(string Code);

    [Test]
    public async Task CommandLine_Register_SavesConfig()
    {
        using var root = new AgentConfigStoreTests.TempRoot();
        var code = await (await (await AdminAsync()).PostAsync("/api/agents/registration-codes", null)).ReadJsonAsync<CodeBody>();
        var output = new StringWriter();

        var exitCode = await AgentCommandLine.RunAsync(["--register", "--server", "http://localhost", "--code", code.Code], output,
            new Registration(Server.Server.CreateHandler()));

        exitCode.ShouldBe(0);
        var config = AgentConfigStore.Load().ShouldNotBeNull();
        config.ServerUrl.ShouldBe("http://localhost");
        config.TrustAddin.ShouldBeFalse(); // без явного согласия Revit спрашивает, как обычно
        output.ToString().ShouldContain("зарегистрирован");
    }

    [Test]
    public async Task CommandLine_Register_TrustAddin_Saved()
    {
        using var root = new AgentConfigStoreTests.TempRoot();
        var code = await (await (await AdminAsync()).PostAsync("/api/agents/registration-codes", null)).ReadJsonAsync<CodeBody>();

        var exitCode = await AgentCommandLine.RunAsync(["--register", "--server", "http://localhost", "--code", code.Code, "--trust-addin"],
            new StringWriter(), new Registration(Server.Server.CreateHandler()));

        exitCode.ShouldBe(0);
        AgentConfigStore.Load().ShouldNotBeNull().TrustAddin.ShouldBeTrue();
    }

    [Test]
    public async Task CommandLine_Register_BadCode_ExitCode1()
    {
        using var root = new AgentConfigStoreTests.TempRoot();
        var output = new StringWriter();

        var exitCode = await AgentCommandLine.RunAsync(["--register", "--server", "http://localhost", "--code", "TF-XXXX-YYYY"], output,
            new Registration(Server.Server.CreateHandler()));

        exitCode.ShouldBe(1);
        output.ToString().ShouldContain("Код недействителен");
        AgentConfigStore.Load().ShouldBeNull();
    }

    [Test]
    public async Task CommandLine_Register_MissingArguments_ExitCode2()
    {
        var output = new StringWriter();

        (await AgentCommandLine.RunAsync(["--register", "--server", "http://localhost"], output)).ShouldBe(2);
        output.ToString().ShouldContain("--code");
    }

    [Test]
    public async Task CommandLine_NoRegister_ReturnsNull() =>
        (await AgentCommandLine.RunAsync(["--autostart"], new StringWriter())).ShouldBeNull();
}
