using System.Net;
using System.Net.Http.Json;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Shouldly;
using ToadFlow.Contracts;
using ToadFlow.Server.Tests.Infrastructure;
using ToadFlow.Server.Usage.Data;
using static ToadFlow.Server.Tests.Infrastructure.UsageFixtures;

namespace ToadFlow.Server.Tests;

public class UsageIngestTests : ServerTest
{
    private Task<List<T>> RowsAsync<T>() where T : class => DbAsync(db => db.Set<T>().AsNoTracking().ToListAsync());

    [Test]
    public async Task Designer_CanSend()
    {
        var designer = await ClientAsync(UserRole.Designer);

        var answer = await SendAsync(designer, Operation());

        answer.ShouldBe(new IngestAnswer(1, 0));
    }

    [Test]
    public async Task Designer_CanSend_ByPersonalToken()
    {
        var designer = await ClientAsync(UserRole.Designer);
        var created = await (await designer.PostAsJsonAsync("/api/auth/tokens", new { name = "ToadTools" }, TestJson.Options))
            .Content.ReadFromJsonAsync<TokenBody>(TestJson.Options);
        var client = Server.CreateClient();
        client.DefaultRequestHeaders.Add("X-ToadFlow-Token", created!.Token);

        (await SendAsync(client, Operation())).Accepted.ShouldBe(1);
    }

    private sealed record TokenBody(string Token);

    [Test]
    public async Task Operation_Twice_StoredOnce()
    {
        var client = await ClientAsync(UserRole.Designer);
        var operation = Operation();

        (await SendAsync(client, operation)).ShouldBe(new IngestAnswer(1, 0));
        (await SendAsync(client, operation)).ShouldBe(new IngestAnswer(0, 1));

        (await RowsAsync<UsageOperation>()).Count.ShouldBe(1);
    }

    [Test]
    public async Task Session_Running_ThenNormal_Replaced()
    {
        var client = await ClientAsync(UserRole.Designer);
        var id = Guid.NewGuid();
        var started = DateTimeOffset.UtcNow.AddHours(-3);

        await SendAsync(client, Session(id, startedAt: started, seenAt: started.AddHours(1), activeSeconds: 100));
        await SendAsync(client, Session(id, startedAt: started, seenAt: started.AddHours(2), end: "Normal", activeSeconds: 200));
        await SendAsync(client, Session(id, startedAt: started, seenAt: started.AddMinutes(90), activeSeconds: 150));

        var session = (await RowsAsync<UsageSession>()).ShouldHaveSingleItem();
        session.End.ShouldBe("Normal");
        session.ActiveSeconds.ShouldBe(200);
        session.SeenAt.ShouldBe(started.AddHours(2), TimeSpan.FromMilliseconds(1));
    }

    [Test]
    public async Task Counters_NeverDecrease()
    {
        var client = await ClientAsync(UserRole.Designer);
        var session = Guid.NewGuid();
        var day = DateOnly.FromDateTime(DateTime.Today);

        await SendAsync(client, CommandDay(session, day, count: 5, failed: 2), SessionDay(session, day, 600));
        await SendAsync(client, CommandDay(session, day, count: 3, failed: 1), SessionDay(session, day, 300));

        var command = (await RowsAsync<UsageCommandDay>()).ShouldHaveSingleItem();
        command.Count.ShouldBe(5);
        command.Failed.ShouldBe(2);
        (await RowsAsync<UsageSessionDay>()).ShouldHaveSingleItem().ActiveSeconds.ShouldBe(600);

        await SendAsync(client, CommandDay(session, day, count: 7, failed: 2));
        (await RowsAsync<UsageCommandDay>()).ShouldHaveSingleItem().Count.ShouldBe(7);
    }

    [Test]
    public async Task Machine_Replaced_FirstSeenKept()
    {
        var client = await ClientAsync(UserRole.Designer);
        var first = DateTimeOffset.UtcNow.AddDays(-5);
        var second = DateTimeOffset.UtcNow.AddHours(-1);

        await SendAsync(client, Machine(at: first, ramMb: 16384, toadTools: "4.6.0"));
        await SendAsync(client, Machine(name: "ws-01", at: second, ramMb: 65536, toadTools: "4.6.1"));

        var machine = (await RowsAsync<UsageMachine>()).ShouldHaveSingleItem();
        machine.Key.ShouldBe("ws-01");
        machine.RamMb.ShouldBe(65536);
        machine.ToadTools.ShouldBe("4.6.1");
        machine.RevitYears.ShouldBe([2022, 2024]);
        machine.FirstSeenAt.ShouldBe(first, TimeSpan.FromMilliseconds(1));
        machine.LastSeenAt.ShouldBe(second, TimeSpan.FromMilliseconds(1));
        machine.LastUserId.ShouldNotBeNull();
    }

    [Test]
    public async Task Session_UpdatesMachineLastSeen()
    {
        var client = await ClientAsync(UserRole.Designer);
        await SendAsync(client, Machine(at: DateTimeOffset.UtcNow.AddDays(-3)));
        var seen = DateTimeOffset.UtcNow.AddMinutes(-5);

        await SendAsync(client, Session(startedAt: seen.AddHours(-1), seenAt: seen));

        (await RowsAsync<UsageMachine>()).ShouldHaveSingleItem().LastSeenAt.ShouldBe(seen, TimeSpan.FromMilliseconds(1));
    }

    [Test]
    public async Task UnknownType_And_Garbage_Skipped()
    {
        var client = await ClientAsync(UserRole.Designer);
        var noId = Operation();
        noId.Remove("id");

        var answer = await SendAsync(client,
            Operation(),
            new System.Text.Json.Nodes.JsonObject { ["type"] = "future", ["id"] = Guid.NewGuid() },
            Operation(durationMs: -5),
            Operation(durationMs: 25L * 60 * 60 * 1000),
            Operation(startedAt: DateTimeOffset.UtcNow.AddDays(2)),
            noId);

        answer.ShouldBe(new IngestAnswer(1, 5));
        (await RowsAsync<UsageOperation>()).Count.ShouldBe(1);
    }

    [Test]
    public async Task UnknownFields_Ignored()
    {
        var client = await ClientAsync(UserRole.Designer);
        var operation = Operation();
        operation["somethingNew"] = 42;

        (await SendAsync(client, operation)).Accepted.ShouldBe(1);
    }

    [Test]
    public async Task NotJson_400()
    {
        var client = await ClientAsync(UserRole.Designer);

        var response = await client.PostAsync("/api/usage/events", new StringContent("не json", Encoding.UTF8, "application/json"));

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Test]
    public async Task NoItems_400()
    {
        var client = await ClientAsync(UserRole.Designer);

        var response = await client.PostAsJsonAsync("/api/usage/events", new { version = 1 }, TestJson.Options);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Test]
    public async Task TooManyItems_400()
    {
        var client = await ClientAsync(UserRole.Designer);
        var items = Enumerable.Range(0, 201).Select(_ => Operation()).ToArray();

        var response = await client.PostAsJsonAsync("/api/usage/events", Batch(items), TestJson.Options);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await RowsAsync<UsageOperation>()).ShouldBeEmpty();
    }

    [Test]
    public async Task FullBatch_Accepted()
    {
        var client = await ClientAsync(UserRole.Designer);
        var items = Enumerable.Range(0, 200).Select(_ => Operation()).ToArray();

        (await SendAsync(client, items)).Accepted.ShouldBe(200);
    }

    [Test]
    public async Task UnknownVersion_Accepted()
    {
        var client = await ClientAsync(UserRole.Designer);

        var response = await client.PostAsJsonAsync("/api/usage/events", new { version = 7, items = new[] { Operation() } }, TestJson.Options);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await RowsAsync<UsageOperation>()).Count.ShouldBe(1);
    }

    [Test]
    public async Task SentByAnotherAccount_AttributedToSender()
    {
        var a = await ClientAsync(UserRole.Designer);
        var b = await ClientAsync(UserRole.Designer);
        var session = Guid.NewGuid();

        await SendAsync(a, Session(session));
        await SendAsync(b, Operation(sessionId: session));

        var stored = (await RowsAsync<UsageOperation>()).ShouldHaveSingleItem();
        var sessionRow = (await RowsAsync<UsageSession>()).ShouldHaveSingleItem();
        stored.UserId.ShouldNotBe(sessionRow.UserId);
    }

    [Test]
    public async Task Paths_Normalized()
    {
        var client = await ClientAsync(UserRole.Designer);

        await SendAsync(client,
            Operation(modelPath: "RSN://SRV01/Проект/Model.rvt"),
            Operation(kind: "Link", modelPath: @"\\FS\Проекты\Связь.rvt", hostModelPath: "RSN://SRV01/Проект/Model.rvt"));

        var rows = await RowsAsync<UsageOperation>();
        rows.Single(o => o.Kind == "Open").ModelKey.ShouldBe("rsn://srv01/проект/model.rvt");
        var link = rows.Single(o => o.Kind == "Link");
        link.ModelKey.ShouldBe("//fs/проекты/связь.rvt");
        link.HostModelKey.ShouldBe("rsn://srv01/проект/model.rvt");
        link.ModelPath.ShouldBe(@"\\FS\Проекты\Связь.rvt");
    }

    [Test]
    public async Task Anonymous_401()
    {
        var response = await Server.CreateClient().PostAsJsonAsync("/api/usage/events", Batch(Operation()), TestJson.Options);

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }
}
