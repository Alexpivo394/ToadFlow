using System.Buffers.Binary;
using System.Net;
using System.Text.Json;
using Alexpivo394.Revit.Clash.Geometry;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Shouldly;
using ToadFlow.Contracts;
using ToadFlow.Contracts.Clash;
using ToadFlow.Server.Clash;
using ToadFlow.Server.Clash.Collisions;
using ToadFlow.Server.Clash.Engine;
using ToadFlow.Server.Clash.Images;
using ToadFlow.Server.Clash.Parts;
using ToadFlow.Server.Data;
using ToadFlow.Server.Events;
using ToadFlow.Server.Tests.Infrastructure;
using static ToadFlow.Server.Tests.Infrastructure.ClashDraftFixtures;

namespace ToadFlow.Server.Tests;

/// <summary>Картинка коллизии рисуется сервером по запросу и лежит в кеше до смены версии.</summary>
public class ClashImageTests : ServerTest
{
    private const int Pairs = 4;

    private sealed record Setup(HttpClient Admin, Guid PartId, string Ar, string Ov);

    private static ElementInfo Info(string uid) => new(uid, uid.Length, "OST_GenericModel", "Семейство", "Тип", "Этаж 1", null, []);

    /// <summary>
    ///     Часть АР + ОВ на настоящих слепках: в АР кубы <c>a1…a4</c> через 1 м по X и <c>a1n</c> на месте <c>a1</c>, в ОВ кубы
    ///     <c>b1…b4</c>, сдвинутые от пары на 50 мм. Ещё без прогонов.
    /// </summary>
    private async Task<Setup> PartAsync()
    {
        var admin = await AdminAsync();
        await using var agent = await FakeAgent.RegisterAsync(Server, admin);
        await agent.ConnectAsync("1.8.0", new InstalledRevitDto(2022, true));
        ModelGeometry Geometry(string name, string key)
        {
            var header = new ModelHeader(key, name, 2022, DateTimeOffset.UtcNow, true, "Navisworks", 0, 0, 0);
            var ar = name.Contains("_AR");
            var elements = Enumerable.Range(1, Pairs)
                .Select(i => ClashFixtures.Box(Info((ar ? "a" : "b") + i), (i - 1) * 1000 + (ar ? 0 : 50), 0, 0, 100))
                .ToList();
            if (ar) elements.Add(ClashFixtures.Box(Info("a1n"), 0, 0, 0, 100));
            return new ModelGeometry(header, elements.ToArray(), elements.Select(e => e.Info.UniqueId).ToArray());
        }

        var partId = await ClashDraftFixtures.PartAsync(Server, admin, agent, ["POL_K1_AR.rvt", "POL_K1_OV.rvt"], Geometry);
        var models = (await admin.GetJsonAsync<ClashPartView>($"/api/clash/parts/{partId}")).Models;
        return new Setup(admin, partId, models.Single(m => m.Discipline == "АР").ModelId.ToString("N"),
            models.Single(m => m.Discipline == "ОВ").ModelId.ToString("N"));
    }

    private static EngineHit Pair(Setup setup, int i, string? uniqueIdA = null) =>
        Hit(i, setup.Ar, setup.Ov) with { UniqueIdA = uniqueIdA ?? "a" + i, UniqueIdB = "b" + i, X = (i - 1) * 1000 + 75.0, Y = 50, Z = 50, DepthMm = 50 };

    /// <summary>Утверждённый прогон (первый — с утверждением части), сданный одной частью результата.</summary>
    private async Task<Guid> RunApprovedAsync(Setup setup, IReadOnlyList<EngineHit> hits, bool approve = false, IReadOnlyList<EngineGonePair>? gone = null,
        IReadOnlyList<EngineIdentityMatch>? matches = null)
    {
        Guid runId;
        if (approve)
        {
            var response = await setup.Admin.PostJsonAsync($"/api/clash/parts/{setup.PartId}/approve", new { comment = (string?)null, run = true });
            response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
            runId = (await response.ReadJsonAsync<ApprovedVersionView>()).RunId.ShouldNotBeNull();
        }
        else
        {
            using var scope = Server.Services.CreateScope();
            var run = await scope.ServiceProvider.GetRequiredService<ApprovedRuns>().EnqueueAsync(setup.PartId, null, [], Actor.System, CancellationToken.None);
            runId = run.ShouldNotBeNull().Id;
        }

        var engine = EngineClient(Server);
        var claim = await engine.PostAsync("/api/engine/runs/claim", null);
        claim.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await claim.ReadJsonAsync<EngineRunAssignment>()).RunId.ShouldBe(runId);
        var result = await PostApprovedResultAsync(engine, runId, 0, true, hits, Stats, gone ?? [], matches ?? []);
        result.StatusCode.ShouldBe(HttpStatusCode.OK, await result.Content.ReadAsStringAsync());
        return runId;
    }

    private async Task<(Setup Setup, ClashCollision Collision)> OneAsync()
    {
        var setup = await PartAsync();
        await RunApprovedAsync(setup, [Pair(setup, 1)], approve: true);
        return (setup, (await CollisionsAsync(setup.PartId)).Single());
    }

    private Task<List<ClashCollision>> CollisionsAsync(Guid partId) =>
        DbAsync(db => db.Set<ClashCollision>().AsNoTracking().Where(c => c.PartId == partId).OrderBy(c => c.Number).ToListAsync());

    private Task<ClashCollision> CollisionAsync(Guid id) => DbAsync(db => db.Set<ClashCollision>().AsNoTracking().SingleAsync(c => c.Id == id));

    private CollisionImages Images => Server.Services.GetRequiredService<CollisionImages>();

    private string ImageFolder(Guid partId) => Path.Combine(Server.ClashStorageRoot, "images", partId.ToString("N"));

    private string[] FilesOf(Guid partId, Guid collisionId) =>
        Directory.Exists(ImageFolder(partId)) ? Directory.GetFiles(ImageFolder(partId), collisionId.ToString("N") + "*") : [];

    private static async Task<string> VersionAsync(HttpClient client, Guid id) =>
        (await client.GetJsonAsync<CollisionCard>($"/api/clash/collisions/{id}")).Collision.ImageVersion;

    private static (int Width, int Height) PngSize(byte[] png)
    {
        png[..8].ShouldBe(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A });
        return (BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(16)), BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(20)));
    }

    /// <summary>Заголовок как его прислал сервер: разобранный <c>CacheControl</c> переставляет части.</summary>
    private static string CacheControlOf(HttpResponseMessage response) => response.Headers.NonValidated["Cache-Control"].ToString();

    private static async Task<string> ErrorOf(HttpResponseMessage response)
    {
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return json.RootElement.GetProperty("error").GetString()!;
    }

    private async Task DeleteSnapshotsAsync(Guid runId)
    {
        var models = await DbAsync(db => db.Set<EngineRun>().AsNoTracking().Where(r => r.Id == runId).Select(r => r.Models).SingleAsync());
        foreach (var model in models)
            Directory.Delete(Path.Combine(Server.ClashStorageRoot, model.ModelId.ToString("N"), model.SnapshotId.ToString("N")), recursive: true);
    }

    private async Task<ImageTarget> TargetAsync(Guid id)
    {
        await using var scope = Server.Services.CreateAsyncScope();
        var targets = await ImageTargets.LoadAsync(scope.ServiceProvider.GetRequiredService<ToadFlowDb>(), [id], CancellationToken.None);
        return targets[id];
    }

    [Test]
    public async Task ApprovedRun_NoImageWorkQueued()
    {
        await OneAsync();

        var claim = await EngineClient(Server).PostAsync("/api/engine/images/claim", null);

        claim.IsSuccessStatusCode.ShouldBeFalse();
    }

    [Test]
    public async Task Image_FirstRequest_RendersPng640x400_ThenServedFromCache()
    {
        var (setup, collision) = await OneAsync();
        var version = await VersionAsync(setup.Admin, collision.Id);
        version.ShouldBe(ImageTarget.VersionOf(collision));
        var designer = await ClientAsync(UserRole.Designer, "ОВ");

        var first = await designer.GetAsync($"/api/clash/collisions/{collision.Id}/image?v={version}");

        first.StatusCode.ShouldBe(HttpStatusCode.OK, await first.Content.ReadAsStringAsync());
        first.Content.Headers.ContentType!.MediaType.ShouldBe("image/png");
        CacheControlOf(first).ShouldBe(ImageEndpoints.CacheControl);
        var png = await first.Content.ReadAsByteArrayAsync();
        PngSize(png).ShouldBe((640, 400));
        FilesOf(setup.PartId, collision.Id).Select(Path.GetFileName).ShouldBe([$"{collision.Id:N}-{version}.png"]);

        var second = await designer.GetAsync($"/api/clash/collisions/{collision.Id}/image?v={version}");
        second.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await second.Content.ReadAsByteArrayAsync()).ShouldBe(png);
        Images.Renders.ShouldBe(1);
    }

    [Test]
    public async Task Image_ConcurrentSameCollision_RenderedOnce()
    {
        var (setup, collision) = await OneAsync();

        var responses = await Task.WhenAll(Enumerable.Range(0, 20).Select(_ => setup.Admin.GetAsync($"/api/clash/collisions/{collision.Id}/image")));

        responses.ShouldAllBe(r => r.StatusCode == HttpStatusCode.OK);
        foreach (var response in responses) PngSize(await response.Content.ReadAsByteArrayAsync()).ShouldBe((640, 400));
        Images.Renders.ShouldBe(1);
    }

    [Test]
    public async Task Image_WaiterCancelled_OthersStillGetImage()
    {
        var (setup, collision) = await OneAsync();
        var target = await TargetAsync(collision.Id);
        using var cancel = new CancellationTokenSource();

        var first = Images.GetAsync(target, cancel.Token);
        cancel.Cancel();
        await Should.ThrowAsync<OperationCanceledException>(() => first);
        var second = await Images.GetAsync(target, CancellationToken.None);

        (second.Status, second.Error).ShouldBe((200, null));
        PngSize(await File.ReadAllBytesAsync(second.Path!)).ShouldBe((640, 400));
        Images.Renders.ShouldBe(1);
        (await setup.Admin.GetAsync($"/api/clash/collisions/{collision.Id}/image")).StatusCode.ShouldBe(HttpStatusCode.OK);
        Directory.GetFiles(ImageFolder(setup.PartId), "*.tmp").ShouldBeEmpty();
    }

    [Test]
    public async Task Image_NoEvents()
    {
        var (setup, collision) = await OneAsync();
        var token = await TokenAsync(ServerFactory.AdminLogin, ServerFactory.AdminPassword);
        await using var hub = new HubConnectionBuilder()
            .WithUrl(new Uri(Server.Server.BaseAddress, "/hubs/ui"), options =>
            {
                options.Transports = HttpTransportType.LongPolling;
                options.HttpMessageHandlerFactory = _ => Server.Server.CreateHandler();
                options.AccessTokenProvider = () => Task.FromResult<string?>(token);
            })
            .Build();
        var collisionsChanged = 0;
        var markers = new HashSet<string>();
        hub.On<string, string>("changed", (entity, id) =>
        {
            lock (markers)
            {
                if (entity == "clash-collisions" && id == setup.PartId.ToString()) collisionsChanged++;
                if (entity == "runs") markers.Add(id);
            }
        });
        await hub.StartAsync();
        var events = Server.Services.GetRequiredService<IDomainEvents>();

        // Рассылка доходит до подключения чуть позже рукопожатия (см. EventsTests.UiHub_ReceivesChanged): метка, пока не дойдёт.
        async Task MarkAsync()
        {
            var marker = Guid.NewGuid().ToString();
            for (var attempt = 0; attempt < 50; attempt++)
            {
                events.Publish(new RunChanged(Actor.System, Guid.Parse(marker)));
                await Task.Delay(100);
                lock (markers)
                    if (markers.Contains(marker))
                        return;
            }

            throw new TimeoutException("Метка не дошла до подписчика");
        }

        await MarkAsync();
        lock (markers) collisionsChanged = 0;

        (await setup.Admin.GetAsync($"/api/clash/collisions/{collision.Id}/image")).StatusCode.ShouldBe(HttpStatusCode.OK);
        Images.Renders.ShouldBe(1);
        await MarkAsync(); // события идут по порядку: всё, что опубликовала отрисовка, пришло бы раньше метки

        lock (markers) collisionsChanged.ShouldBe(0);
    }

    [Test]
    public async Task Image_NewRun_NewVersion_OldFileRemoved()
    {
        var (setup, collision) = await OneAsync();
        var oldVersion = await VersionAsync(setup.Admin, collision.Id);
        (await setup.Admin.GetAsync($"/api/clash/collisions/{collision.Id}/image?v={oldVersion}")).StatusCode.ShouldBe(HttpStatusCode.OK);

        await RunApprovedAsync(setup, [Pair(setup, 1)]);

        var newVersion = await VersionAsync(setup.Admin, collision.Id);
        newVersion.ShouldNotBe(oldVersion);
        var response = await setup.Admin.GetAsync($"/api/clash/collisions/{collision.Id}/image?v={newVersion}");
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        Images.Renders.ShouldBe(2);
        FilesOf(setup.PartId, collision.Id).Select(Path.GetFileName).ShouldBe([$"{collision.Id:N}-{newVersion}.png"]);
    }

    /// <summary>
    ///     Ошибка «старая картинка после объединения»: прежде у оставшейся оставались картинка и версия, перенесённые с удалённой
    ///     (или её собственные устаревшие), а файл удалённой — на диске.
    /// </summary>
    [Test]
    public async Task Image_MergeRecreated_SurvivorGetsFreshImage()
    {
        var setup = await PartAsync();
        await RunApprovedAsync(setup, [Pair(setup, 1)], approve: true);
        var old = (await CollisionsAsync(setup.PartId)).Single();
        var oldVersion = await VersionAsync(setup.Admin, old.Id);
        (await setup.Admin.GetAsync($"/api/clash/collisions/{old.Id}/image")).StatusCode.ShouldBe(HttpStatusCode.OK);
        await RunApprovedAsync(setup, [Pair(setup, 1, uniqueIdA: "a1n")],
            gone: [new EngineGonePair(setup.Ar, "a1", setup.Ov, "b1", "Absent", "InGeometry", null)],
            matches: [new EngineIdentityMatch(setup.Ar, "a1", "a1n")]);
        var recreated = (await CollisionsAsync(setup.PartId)).Single(c => c.Id != old.Id);
        var recreatedVersion = await VersionAsync(setup.Admin, recreated.Id);
        (await setup.Admin.GetAsync($"/api/clash/collisions/{recreated.Id}/image?v={recreatedVersion}")).StatusCode.ShouldBe(HttpStatusCode.OK);
        FilesOf(setup.PartId, recreated.Id).ShouldNotBeEmpty();

        var merge = await setup.Admin.PostJsonAsync($"/api/clash/collisions/{recreated.Id}/merge", new { predecessorId = old.Id });
        merge.StatusCode.ShouldBe(HttpStatusCode.OK, await merge.Content.ReadAsStringAsync());

        var survivor = await CollisionAsync(old.Id);
        survivor.UniqueIdA.ShouldBe("a1n");
        var version = await VersionAsync(setup.Admin, old.Id);
        version.ShouldBe(ImageTarget.VersionOf(survivor));
        version.ShouldNotBe(oldVersion);
        FilesOf(setup.PartId, recreated.Id).ShouldBeEmpty();
        var response = await setup.Admin.GetAsync($"/api/clash/collisions/{old.Id}/image?v={version}");
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        CacheControlOf(response).ShouldBe(ImageEndpoints.CacheControl);
        FilesOf(setup.PartId, old.Id).Select(Path.GetFileName).ShouldBe([$"{old.Id:N}-{version}.png"]);
    }

    [Test]
    public async Task Image_ElementMissingInSnapshot_404WithReason()
    {
        var (setup, collision) = await OneAsync();
        await DbAsync(db => db.Set<ClashCollision>().Where(c => c.Id == collision.Id).ExecuteUpdateAsync(c => c.SetProperty(x => x.UniqueIdA, "missing-uid")));

        var response = await setup.Admin.GetAsync($"/api/clash/collisions/{collision.Id}/image");

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await ErrorOf(response)).ShouldContain("missing-uid");
        FilesOf(setup.PartId, collision.Id).ShouldBeEmpty();
    }

    [Test]
    public async Task Image_SnapshotDeleted_CachedServed()
    {
        var (setup, collision) = await OneAsync();
        var png = await setup.Admin.GetByteArrayAsync($"/api/clash/collisions/{collision.Id}/image");

        await DeleteSnapshotsAsync(collision.LastFoundRunId);

        var response = await setup.Admin.GetAsync($"/api/clash/collisions/{collision.Id}/image");
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await response.Content.ReadAsByteArrayAsync()).ShouldBe(png);
    }

    [Test]
    public async Task Image_SnapshotDeleted_NoCache_Gone()
    {
        var (setup, collision) = await OneAsync();

        await DeleteSnapshotsAsync(collision.LastFoundRunId);

        var response = await setup.Admin.GetAsync($"/api/clash/collisions/{collision.Id}/image");
        response.StatusCode.ShouldBe(HttpStatusCode.Gone);
        (await ErrorOf(response)).ShouldContain("выгрузка модели удалена");
        Images.Renders.ShouldBe(0);
    }

    [Test]
    public async Task Image_StaleVersionInUrl_ServedCurrentWithoutImmutableCache()
    {
        var (setup, collision) = await OneAsync();
        var png = await setup.Admin.GetByteArrayAsync($"/api/clash/collisions/{collision.Id}/image?v={await VersionAsync(setup.Admin, collision.Id)}");

        var response = await setup.Admin.GetAsync($"/api/clash/collisions/{collision.Id}/image?v=0123456789abcdef");

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await response.Content.ReadAsByteArrayAsync()).ShouldBe(png);
        CacheControlOf(response).ShouldBe("no-cache");
        CacheControlOf(await setup.Admin.GetAsync($"/api/clash/collisions/{collision.Id}/image")).ShouldBe("no-cache");
    }

    [Test]
    public async Task Image_ThreadsLimit_Respected()
    {
        var setup = await PartAsync();
        await RunApprovedAsync(setup, Enumerable.Range(1, Pairs).Select(i => Pair(setup, i)).ToList(), approve: true);
        var ids = (await CollisionsAsync(setup.PartId)).Select(c => c.Id).ToList();
        ids.Count.ShouldBe(Pairs);
        await using var limited = Server.WithWebHostBuilder(builder => builder.UseSetting("Clash:ImageThreads", "1"));
        limited.Services.GetRequiredService<IOptions<CollisionImageOptions>>().Value.Threads.ShouldBe(1);
        var client = limited.CreateClient();
        client.DefaultRequestHeaders.Authorization = setup.Admin.DefaultRequestHeaders.Authorization;

        var responses = await Task.WhenAll(ids.Select(id => client.GetAsync($"/api/clash/collisions/{id}/image")));

        responses.ShouldAllBe(r => r.StatusCode == HttpStatusCode.OK);
        var images = limited.Services.GetRequiredService<CollisionImages>();
        images.Renders.ShouldBe(Pairs);
        images.MaxConcurrentRenders.ShouldBe(1);
    }
}
