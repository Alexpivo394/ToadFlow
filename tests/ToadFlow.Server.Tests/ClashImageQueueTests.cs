using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using Shouldly;
using ToadFlow.Contracts;
using ToadFlow.Contracts.Clash;
using ToadFlow.Server.Clash;
using ToadFlow.Server.Clash.Collisions;
using ToadFlow.Server.Clash.Engine;
using ToadFlow.Server.Clash.Images;
using ToadFlow.Server.Clash.Parts;
using ToadFlow.Server.Data.Entities;
using ToadFlow.Server.Events;
using ToadFlow.Server.Tests.Infrastructure;
using static ToadFlow.Server.Tests.Infrastructure.ClashDraftFixtures;

namespace ToadFlow.Server.Tests;

public class ClashImageQueueTests : ServerTest
{
    private static readonly byte[] TinyPng = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 1, 2, 3, 4];

    private readonly FakeTimeProvider _time = new(DateTimeOffset.UtcNow);

    public ClashImageQueueTests() => Time = _time;

    private async Task<(HttpClient Admin, FakeAgent Agent, Guid PartId, HttpClient Engine)> SetupAsync()
    {
        var admin = await AdminAsync();
        var agent = await FakeAgent.RegisterAsync(Server, admin);
        await agent.ConnectAsync("1.8.0", new InstalledRevitDto(2022, true));
        var partId = await PartAsync(Server, admin, agent, "POL_K1_AR.rvt", "POL_K1_OV.rvt");
        return (admin, agent, partId, EngineClient(Server));
    }

    /// <summary>Утверждённый прогон части (первый — с утверждением), сданный работником одной частью.</summary>
    private async Task<Guid> RunApprovedAsync(HttpClient admin, HttpClient engine, Guid partId, IReadOnlyList<EngineHit> hits, bool approve = false,
        IReadOnlyList<EngineGonePair>? gone = null, IReadOnlyList<EngineIdentityMatch>? matches = null)
    {
        Guid runId;
        if (approve)
        {
            var response = await admin.PostJsonAsync($"/api/clash/parts/{partId}/approve", new { comment = (string?)null, run = true });
            response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
            runId = (await response.ReadJsonAsync<ApprovedVersionView>()).RunId.ShouldNotBeNull();
        }
        else runId = await EnqueueAsync(partId);

        var claim = await engine.PostAsync("/api/engine/runs/claim", null);
        claim.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await claim.ReadJsonAsync<EngineRunAssignment>()).RunId.ShouldBe(runId);
        var result = await PostApprovedResultAsync(engine, runId, 0, true, hits, Stats, gone ?? [], matches ?? []);
        result.StatusCode.ShouldBe(HttpStatusCode.OK, await result.Content.ReadAsStringAsync());
        return runId;
    }

    private async Task<Guid> EnqueueAsync(Guid partId)
    {
        using var scope = Server.Services.CreateScope();
        var run = await scope.ServiceProvider.GetRequiredService<ApprovedRuns>().EnqueueAsync(partId, null, [], Actor.System, CancellationToken.None);
        return run.ShouldNotBeNull().Id;
    }

    private static EngineHit Fp(EngineHit hit, long a = 11, long b = 22) => hit with { FingerprintA = a, FingerprintB = b };

    private static async Task<EngineImageAssignment> ClaimImagesAsync(HttpClient engine)
    {
        var response = await engine.PostAsync("/api/engine/images/claim", null);
        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        return await response.ReadJsonAsync<EngineImageAssignment>();
    }

    private static async Task<HttpStatusCode> ClaimImagesStatusAsync(HttpClient engine) => (await engine.PostAsync("/api/engine/images/claim", null)).StatusCode;

    private static Task<HttpResponseMessage> BatchAsync(HttpClient engine, Guid partId, Guid runId) =>
        engine.GetAsync($"/api/engine/images/{partId}/batch?runId={runId}");

    private static async Task<EngineImageBatch> BatchOkAsync(HttpClient engine, Guid partId, Guid runId)
    {
        var response = await BatchAsync(engine, partId, runId);
        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        return await response.ReadJsonAsync<EngineImageBatch>();
    }

    private static Task<HttpResponseMessage> UploadAsync(HttpClient engine, Guid partId, Guid runId, IEnumerable<(EngineImageRequest Item, bool Ok)> items)
    {
        var list = items.ToList();
        var content = new MultipartFormDataContent();
        var manifest = list.Select(i => new EngineImageResult(i.Item.CollisionId, i.Item.FingerprintA, i.Item.FingerprintB, i.Ok)).ToList();
        content.Add(new StringContent(JsonSerializer.Serialize(manifest, TestJson.Options), Encoding.UTF8, "application/json"), EngineApi.ImageManifestPart);
        foreach (var (item, ok) in list.Where(i => i.Ok))
        {
            var file = new ByteArrayContent(TinyPng);
            file.Headers.ContentType = new MediaTypeHeaderValue("image/png");
            content.Add(file, "images", $"{item.CollisionId:N}.png");
        }

        return engine.PostAsync($"/api/engine/images/{partId}?runId={runId}", content);
    }

    /// <summary>Берёт задание и сдаёт картинки всех коллизий пачки, пока пачки не кончатся; закрывает задание.</summary>
    private static async Task DrawAllAsync(HttpClient engine, Guid partId, Guid runId)
    {
        (await ClaimImagesAsync(engine)).RunId.ShouldBe(runId);
        while ((await BatchOkAsync(engine, partId, runId)).Items is { Count: > 0 } items)
            (await UploadAsync(engine, partId, runId, items.Select(i => (i, true)))).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await engine.PostAsync($"/api/engine/images/{partId}/done?runId={runId}", null)).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    private Task<List<ClashCollision>> CollisionsAsync(Guid partId) =>
        DbAsync(db => db.Set<ClashCollision>().AsNoTracking().Where(c => c.PartId == partId).OrderBy(c => c.Number).ToListAsync());

    private Task<ClashImageJob?> JobAsync(Guid partId) => DbAsync(db => db.Set<ClashImageJob>().AsNoTracking().SingleOrDefaultAsync(j => j.PartId == partId));

    private string ImagePath(Guid partId, Guid collisionId)
    {
        using var scope = Server.Services.CreateScope();
        return scope.ServiceProvider.GetRequiredService<ClashStorage>().ImagePath(partId, collisionId);
    }

    [Test]
    public async Task Reconcile_SetsFirstRunIdAndRunIdInFeed_QueuesImageJob()
    {
        var (admin, agent, partId, engine) = await SetupAsync();
        await using var _ = agent;

        var first = await RunApprovedAsync(admin, engine, partId, [Fp(Hit(1)), Fp(Hit(2))], approve: true);
        (await JobAsync(partId)).ShouldNotBeNull().RunId.ShouldBe(first);
        var second = await RunApprovedAsync(admin, engine, partId, [Fp(Hit(1))]);

        var collisions = await CollisionsAsync(partId);
        collisions.ShouldAllBe(c => c.FirstRunId == first);
        var feed = await DbAsync(db => db.Set<ClashCollisionEvent>().AsNoTracking().ToListAsync());
        feed.Where(e => e.Kind == CollisionEventKind.Found && e.Version == 1).ShouldAllBe(e => e.RunId == first);
        var fixedEvent = feed.Single(e => e is { Kind: CollisionEventKind.Status, ToStatus: CollisionStatus.Fixed });
        (fixedEvent.CollisionId, fixedEvent.RunId).ShouldBe((collisions[1].Id, (Guid?)second));
        feed.Single(e => e is { Kind: CollisionEventKind.Status, ToStatus: CollisionStatus.Active }).RunId.ShouldBe(second);
        var job = (await JobAsync(partId)).ShouldNotBeNull();
        (job.RunId, job.Attempt, job.LeaseUntil).ShouldBe((second, 0, (DateTimeOffset?)null));
    }

    [Test]
    public async Task Images_ClaimWaitsForEngineRuns()
    {
        var (admin, agent, partId, engine) = await SetupAsync();
        await using var _ = agent;
        await RunApprovedAsync(admin, engine, partId, [Fp(Hit(1))], approve: true);

        await EnqueueAsync(partId);

        (await ClaimImagesStatusAsync(engine)).ShouldBe(HttpStatusCode.NoContent);
        (await JobAsync(partId)).ShouldNotBeNull().Attempt.ShouldBe(0);
    }

    [Test]
    public async Task Images_Batch_OnlyFoundByJobRunWithStaleImage()
    {
        var (admin, agent, partId, engine) = await SetupAsync();
        await using var _ = agent;
        var first = await RunApprovedAsync(admin, engine, partId, [Fp(Hit(1)), Fp(Hit(2))], approve: true);
        await DrawAllAsync(engine, partId, first);

        var second = await RunApprovedAsync(admin, engine, partId, [Fp(Hit(1)), Fp(Hit(3))]);

        var assignment = await ClaimImagesAsync(engine);
        (assignment.PartId, assignment.RunId).ShouldBe((partId, second));
        assignment.Models.Count.ShouldBe(2);
        var batch = await BatchOkAsync(engine, partId, second);
        var item = batch.Items.ShouldHaveSingleItem();
        item.UniqueIdA.ShouldBe("ua-3");
        (item.ModelA, item.ModelB, item.X, item.FingerprintA, item.FingerprintB).ShouldBe(("a", "b", 3d, 11L, 22L));
    }

    [Test]
    public async Task Images_Upload_StoresFileAndColumns_ThenBatchEmpty_Done()
    {
        var (admin, agent, partId, engine) = await SetupAsync();
        await using var _ = agent;
        var runId = await RunApprovedAsync(admin, engine, partId, [Fp(Hit(1)), Fp(Hit(2))], approve: true);
        await ClaimImagesAsync(engine);

        var batch = await BatchOkAsync(engine, partId, runId);
        batch.Items.Count.ShouldBe(2);
        var upload = await UploadAsync(engine, partId, runId, batch.Items.Select(i => (i, true)));

        upload.StatusCode.ShouldBe(HttpStatusCode.OK, await upload.Content.ReadAsStringAsync());
        foreach (var c in await CollisionsAsync(partId))
        {
            (c.HasImage, c.ImageFingerprintA, c.ImageFingerprintB).ShouldBe((true, (long?)11, (long?)22));
            (await File.ReadAllBytesAsync(ImagePath(partId, c.Id))).ShouldBe(TinyPng);
        }

        Directory.GetFiles(Path.GetDirectoryName(ImagePath(partId, Guid.Empty))!, "*.tmp").ShouldBeEmpty();
        (await BatchOkAsync(engine, partId, runId)).Items.ShouldBeEmpty();
        (await engine.PostAsync($"/api/engine/images/{partId}/done?runId={runId}", null)).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await JobAsync(partId)).ShouldBeNull();
        (await ClaimImagesStatusAsync(engine)).ShouldBe(HttpStatusCode.NoContent);
    }

    [Test]
    public async Task Images_FailedItem_NoImageNotRequeued()
    {
        var (admin, agent, partId, engine) = await SetupAsync();
        await using var _ = agent;
        var runId = await RunApprovedAsync(admin, engine, partId, [Fp(Hit(1)), Fp(Hit(2))], approve: true);
        await ClaimImagesAsync(engine);
        var batch = await BatchOkAsync(engine, partId, runId);

        (await UploadAsync(engine, partId, runId, batch.Items.Select(i => (i, i.UniqueIdA != "ua-2")))).StatusCode.ShouldBe(HttpStatusCode.OK);

        var failed = (await CollisionsAsync(partId)).Single(c => c.UniqueIdA == "ua-2");
        (failed.HasImage, failed.ImageFingerprintA, failed.ImageFingerprintB).ShouldBe((false, (long?)11, (long?)22));
        File.Exists(ImagePath(partId, failed.Id)).ShouldBeFalse();
        (await BatchOkAsync(engine, partId, runId)).Items.ShouldBeEmpty();
    }

    [Test]
    public async Task Images_FingerprintChanged_Requeued()
    {
        var (admin, agent, partId, engine) = await SetupAsync();
        await using var _ = agent;
        await DrawAllAsync(engine, partId, await RunApprovedAsync(admin, engine, partId, [Fp(Hit(1))], approve: true));

        var second = await RunApprovedAsync(admin, engine, partId, [Fp(Hit(1), a: 33)]);

        (await ClaimImagesAsync(engine)).RunId.ShouldBe(second);
        var item = (await BatchOkAsync(engine, partId, second)).Items.ShouldHaveSingleItem();
        (item.FingerprintA, item.FingerprintB).ShouldBe((33L, 22L));
    }

    [Test]
    public async Task Images_NewerRunDuringJob_UploadRejected()
    {
        var (admin, agent, partId, engine) = await SetupAsync();
        await using var _ = agent;
        var first = await RunApprovedAsync(admin, engine, partId, [Fp(Hit(1))], approve: true);
        await ClaimImagesAsync(engine);
        var stale = (await BatchOkAsync(engine, partId, first)).Items;

        var second = await RunApprovedAsync(admin, engine, partId, [Fp(Hit(1), a: 33)]);

        (await BatchAsync(engine, partId, first)).StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await UploadAsync(engine, partId, first, stale.Select(i => (i, true)))).StatusCode.ShouldBe(HttpStatusCode.Conflict);
        var collision = (await CollisionsAsync(partId)).Single();
        File.Exists(ImagePath(partId, collision.Id)).ShouldBeFalse();
        (collision.HasImage, collision.ImageFingerprintA).ShouldBe((false, (long?)null));
        (await engine.PostAsync($"/api/engine/images/{partId}/done?runId={first}", null)).StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await ClaimImagesAsync(engine)).RunId.ShouldBe(second);
    }

    [Test]
    public async Task Images_LeaseExpired_ReclaimedThenDroppedAfter3WithEvent()
    {
        var (admin, agent, partId, engine) = await SetupAsync();
        await using var _ = agent;
        await RunApprovedAsync(admin, engine, partId, [Fp(Hit(1))], approve: true);

        await ClaimImagesAsync(engine);
        (await ClaimImagesStatusAsync(engine)).ShouldBe(HttpStatusCode.NoContent); // аренда держит задание
        for (var attempt = 2; attempt <= 3; attempt++)
        {
            _time.Advance(ClashImageJob.Lease + TimeSpan.FromSeconds(1));
            await ClaimImagesAsync(engine);
            (await JobAsync(partId)).ShouldNotBeNull().Attempt.ShouldBe(attempt);
        }

        _time.Advance(ClashImageJob.Lease + TimeSpan.FromSeconds(1));
        (await ClaimImagesStatusAsync(engine)).ShouldBe(HttpStatusCode.NoContent);
        (await JobAsync(partId)).ShouldBeNull();
        await EventuallyAsync(() => DbAsync(db => db.SystemEvents.AnyAsync(e => e.Kind == nameof(ClashImagesFailed))));
        (await DbAsync(db => db.SystemEvents.SingleAsync(e => e.Kind == nameof(ClashImagesFailed)))).Text.ShouldContain("Часть");
    }

    [Test]
    public async Task Images_Fail_ReleasesLease_YieldDoesNotCountAttempt()
    {
        var (admin, agent, partId, engine) = await SetupAsync();
        await using var _ = agent;
        var runId = await RunApprovedAsync(admin, engine, partId, [Fp(Hit(1))], approve: true);

        await ClaimImagesAsync(engine);
        (await engine.PostJsonAsync($"/api/engine/images/{partId}/fail?runId={runId}", new EngineImageFailure("ждёт прогон", Yield: true)))
            .StatusCode.ShouldBe(HttpStatusCode.OK);
        (await JobAsync(partId)).ShouldNotBeNull().Attempt.ShouldBe(0);

        await ClaimImagesAsync(engine);
        (await engine.PostJsonAsync($"/api/engine/images/{partId}/fail?runId={runId}", new EngineImageFailure("нет слепка")))
            .StatusCode.ShouldBe(HttpStatusCode.OK);
        var job = (await JobAsync(partId)).ShouldNotBeNull();
        (job.Attempt, job.LeaseUntil, job.Error).ShouldBe((1, (DateTimeOffset?)null, "нет слепка"));
        (await ClaimImagesAsync(engine)).RunId.ShouldBe(runId);
    }

    [Test]
    public async Task Image_Get_MemberOk_404WithoutImage_CacheHeader()
    {
        var (admin, agent, partId, engine) = await SetupAsync();
        await using var _ = agent;
        var runId = await RunApprovedAsync(admin, engine, partId, [Fp(Hit(1))], approve: true);
        var designer = await ClientAsync(UserRole.Designer, "ОВ");
        var id = (await CollisionsAsync(partId)).Single().Id;
        (await designer.GetAsync($"/api/clash/collisions/{id}/image")).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await designer.GetJsonAsync<CollisionCard>($"/api/clash/collisions/{id}")).Collision.ImageVersion.ShouldBeNull();

        await DrawAllAsync(engine, partId, runId);

        var response = await designer.GetAsync($"/api/clash/collisions/{id}/image");
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        response.Content.Headers.ContentType!.MediaType.ShouldBe("image/png");
        var cache = response.Headers.CacheControl.ShouldNotBeNull();
        (cache.Private, cache.MaxAge).ShouldBe((true, (TimeSpan?)TimeSpan.FromDays(365)));
        cache.Extensions.ShouldContain(e => e.Name == "immutable");
        (await response.Content.ReadAsByteArrayAsync()).ShouldBe(TinyPng);
        (await designer.GetJsonAsync<CollisionCard>($"/api/clash/collisions/{id}")).Collision.ImageVersion.ShouldBe($"{11L:x16}{22L:x16}");
    }

    [Test]
    public async Task Merge_DeletesImageOfRemovedCollision()
    {
        var (admin, agent, partId, engine) = await SetupAsync();
        await using var _ = agent;
        await DrawAllAsync(engine, partId, await RunApprovedAsync(admin, engine, partId, [Fp(Hit(1))], approve: true));
        var old = (await CollisionsAsync(partId)).Single();
        var second = await RunApprovedAsync(admin, engine, partId, [Fp(Hit(1)) with { UniqueIdA = "ua-1-new" }],
            gone: [new EngineGonePair("a", "ua-1", "b", "ub-1", "Absent", "InGeometry", null)], matches: [new EngineIdentityMatch("a", "ua-1", "ua-1-new")]);
        await DrawAllAsync(engine, partId, second);
        var recreated = (await CollisionsAsync(partId)).Single(c => c.Id != old.Id);
        File.Exists(ImagePath(partId, recreated.Id)).ShouldBeTrue();

        (await admin.PostJsonAsync($"/api/clash/collisions/{recreated.Id}/merge", new { predecessorId = old.Id })).StatusCode.ShouldBe(HttpStatusCode.OK);

        File.Exists(ImagePath(partId, recreated.Id)).ShouldBeFalse();
        File.Exists(ImagePath(partId, old.Id)).ShouldBeTrue();
    }

    [Test]
    public async Task DeletePart_DeletesImageFolder()
    {
        var (admin, agent, partId, engine) = await SetupAsync();
        await using var _ = agent;
        await DrawAllAsync(engine, partId, await RunApprovedAsync(admin, engine, partId, [Fp(Hit(1))], approve: true));
        var folder = Path.GetDirectoryName(ImagePath(partId, Guid.Empty))!;
        Directory.Exists(folder).ShouldBeTrue();

        (await admin.DeleteAsync($"/api/clash/parts/{partId}")).StatusCode.ShouldBe(HttpStatusCode.NoContent);

        Directory.Exists(folder).ShouldBeFalse();
    }

    [Test]
    public async Task Images_Batch_EngineRunWaiting_EmptyWithFlag()
    {
        var (admin, agent, partId, engine) = await SetupAsync();
        await using var _ = agent;
        var runId = await RunApprovedAsync(admin, engine, partId, [Fp(Hit(1))], approve: true);
        await ClaimImagesAsync(engine);
        (await BatchOkAsync(engine, partId, runId)).EngineRunWaiting.ShouldBeFalse();

        await EnqueueAsync(partId);

        var batch = await BatchOkAsync(engine, partId, runId);
        (batch.EngineRunWaiting, batch.Items.Count).ShouldBe((true, 0));
    }
}
