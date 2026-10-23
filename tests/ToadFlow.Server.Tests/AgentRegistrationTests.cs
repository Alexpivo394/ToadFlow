using System.Net;
using Microsoft.EntityFrameworkCore;
using Shouldly;
using ToadFlow.Contracts;
using ToadFlow.Server.Tests.Infrastructure;

namespace ToadFlow.Server.Tests;

public class AgentRegistrationTests : ServerTest
{
    private sealed record CodeBody(string Code, DateTimeOffset ExpiresAt);

    [Test]
    public async Task Register_ValidCode_ReturnsKey_CodeSingleUse()
    {
        var admin = await AdminAsync();
        var code = await (await admin.PostAsync("/api/agents/registration-codes", null)).ReadJsonAsync<CodeBody>();
        code.Code.ShouldMatch("^TF-[A-HJ-NP-Z2-9]{4}-[A-HJ-NP-Z2-9]{4}$");
        code.ExpiresAt.ShouldBeGreaterThan(DateTimeOffset.UtcNow.AddHours(23));

        var first = await Server.CreateClient().PostJsonAsync("/api/agents/register", new { code = code.Code, machineName = "FARM-01" });
        var second = await Server.CreateClient().PostJsonAsync("/api/agents/register", new { code = code.Code, machineName = "FARM-02" });

        first.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await first.Content.ReadAsStringAsync()).ShouldContain("\"key\"");
        second.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await DbAsync(db => db.Agents.SingleAsync())).KeyHash.Length.ShouldBe(64);
    }

    [Test]
    public async Task Register_ExpiredCode_400()
    {
        var admin = await AdminAsync();
        var code = await (await admin.PostAsync("/api/agents/registration-codes", null)).ReadJsonAsync<CodeBody>();
        await DbAsync(db => db.RegistrationCodes.Where(c => c.Code == code.Code)
            .ExecuteUpdateAsync(c => c.SetProperty(x => x.ExpiresAt, DateTimeOffset.UtcNow.AddMinutes(-1))));

        var response = await Server.CreateClient().PostJsonAsync("/api/agents/register", new { code = code.Code, machineName = "FARM-01" });

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync()).ShouldContain("недействителен");
    }

    [Test]
    public async Task RegistrationCode_OperatorCannotCreate_403()
    {
        var operatorClient = await ClientAsync(UserRole.Operator);

        (await operatorClient.PostAsync("/api/agents/registration-codes", null)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }
}
