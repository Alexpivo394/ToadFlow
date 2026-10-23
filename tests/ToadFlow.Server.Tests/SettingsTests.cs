using System.Net;
using Shouldly;
using ToadFlow.Contracts;
using ToadFlow.Server.Tests.Infrastructure;

namespace ToadFlow.Server.Tests;

public class SettingsTests : ServerTest
{
    private sealed record General(string TimeZoneId, int DefaultModelTimeoutMinutes, int SilenceTimeoutMinutes, int LogRetentionDays,
        int RecycleAfterModels, int RecycleMemoryMb);

    [Test]
    public async Task Settings_Defaults()
    {
        var viewer = await ClientAsync(UserRole.Viewer);

        var settings = await viewer.GetJsonAsync<General>("/api/settings");

        settings.ShouldBe(new General("Europe/Moscow", 30, 10, 90, 20, 12000));
    }

    [Test]
    public async Task Settings_AdminSaves_ThenRead()
    {
        var admin = await AdminAsync();

        (await admin.PutJsonAsync("/api/settings", new General("Asia/Novosibirsk", 45, 15, 30, 10, 16000))).EnsureSuccessStatusCode();

        (await admin.GetJsonAsync<General>("/api/settings")).TimeZoneId.ShouldBe("Asia/Novosibirsk");
    }

    [Test]
    public async Task Settings_InvalidTimeZone_400()
    {
        var admin = await AdminAsync();

        var response = await admin.PutJsonAsync("/api/settings", new General("Mars/Olympus", 30, 10, 90, 20, 12000));

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync()).ShouldContain("часовой пояс");
    }

    [Test]
    public async Task Settings_ZeroTimeout_400()
    {
        var admin = await AdminAsync();

        (await admin.PutJsonAsync("/api/settings", new General("Europe/Moscow", 0, 10, 90, 20, 12000))).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Test]
    public async Task Settings_OperatorCannotSave_403()
    {
        var operatorClient = await ClientAsync(UserRole.Operator);

        (await operatorClient.PutJsonAsync("/api/settings", new General("Europe/Moscow", 30, 10, 90, 20, 12000)))
            .StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }
}
