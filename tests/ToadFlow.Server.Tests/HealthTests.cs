using Shouldly;
using ToadFlow.Server.Tests.Infrastructure;

namespace ToadFlow.Server.Tests;

public class HealthTests : ServerTest
{
    [Test]
    public async Task Health_ReturnsOk()
    {
        var response = await Server.CreateClient().GetAsync("/api/health");

        response.IsSuccessStatusCode.ShouldBeTrue();
        (await response.Content.ReadAsStringAsync()).ShouldContain("ok");
    }
}
