using System.Buffers.Binary;
using Alexpivo394.Revit.Clash.Geometry;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using ToadFlow.Contracts;
using ToadFlow.Contracts.Clash;
using ToadFlow.Server.Clash;
using ToadFlow.Server.Clash.Collisions;
using ToadFlow.Server.Clash.Images;
using ToadFlow.Server.Clash.Parts;
using ToadFlow.Server.Events;
using ToadFlow.Server.Tests.Infrastructure;

namespace ToadFlow.Clash.Worker.Tests;

/// <summary>Картинки коллизий работником после утверждённого прогона, в процессе с сервером.</summary>
public class ImageWorkerTests : ServerTest
{
    private readonly string _cache = Path.Combine(Path.GetTempPath(), "toadflow-engine-cache-tests", Guid.NewGuid().ToString("N"));

    [After(Test)]
    public void CleanCache()
    {
        if (Directory.Exists(_cache)) Directory.Delete(_cache, true);
    }

    /// <summary>Перехват запросов работника: тест вмешивается в задание между пачками.</summary>
    private sealed class Hook(HttpMessageHandler inner) : DelegatingHandler(inner)
    {
        public int Uploads;
        public Func<HttpRequestMessage, Task>? AfterBatch;
        public Func<HttpRequestMessage, Task>? AfterUpload;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var response = await base.SendAsync(request, ct);
            var path = request.RequestUri!.AbsolutePath;
            if (request.Method == HttpMethod.Get && path.EndsWith("/batch") && AfterBatch is { } afterBatch) await afterBatch(request);
            if (request.Method == HttpMethod.Post && path.StartsWith("/api/engine/images/") && path.Count(c => c == '/') == 4 && !path.EndsWith("/claim"))
            {
                Interlocked.Increment(ref Uploads);
                if (AfterUpload is { } afterUpload) await afterUpload(request);
            }

            return response;
        }
    }

    private (EngineWorker Engine, ImageWorker Images, Hook Hook) Workers()
    {
        var hook = new Hook(Server.Server.CreateHandler());
        var http = new HttpClient(hook) { BaseAddress = new Uri("http://localhost/") };
        http.DefaultRequestHeaders.Add(EngineApi.KeyHeader, ServerFactory.EngineKey);
        var client = new EngineClient(http);
        var options = new WorkerOptions { CacheRoot = _cache, Threads = 2 };
        var cache = new SnapshotCache(client, options);
        return (new EngineWorker(client, cache, options, NullLogger<EngineWorker>.Instance),
            new ImageWorker(client, cache, options, NullLogger<ImageWorker>.Instance), hook);
    }

    /// <param name="boxes">null — стена и труба фикстуры (две коллизии); иначе столько совпадающих коробок в АР и ОВ.</param>
    private async Task<(HttpClient Admin, FakeAgent Agent, Guid PartId)> PartAsync(int? boxes = null)
    {
        var admin = await AdminAsync();
        var agent = await FakeAgent.RegisterAsync(Server, admin);
        await agent.ConnectAsync("1.8.0", new InstalledRevitDto(2022, true));
        Func<string, string, ModelGeometry>? custom = boxes is { } count
            ? (name, key) =>
            {
                var header = new ModelHeader(key, $@"C:\models\{name}", 2022, DateTimeOffset.UtcNow, true, "Navisworks", 0, 0, 0);
                var category = name.Contains("_AR") ? "OST_Walls" : "OST_DuctCurves";
                var elements = Enumerable.Range(0, count)
                    .Select(i => ClashFixtures.Box(new ElementInfo($"{name}-{i}", i + 1, category, "Семейство", "Тип", "Этаж 1", null, []), i % 30 * 1000,
                        i / 30 * 1000, 0, 100))
                    .ToList();
                return new ModelGeometry(header, elements, elements.Select(e => e.Info.UniqueId).ToList());
            }
            : null;
        return (admin, agent, await ClashDraftFixtures.PartAsync(Server, admin, agent, ["POL_K1_AR.rvt", "POL_K1_OV.rvt"], custom));
    }

    private static async Task ApproveAsync(HttpClient admin, Guid partId) =>
        (await admin.PostJsonAsync($"/api/clash/parts/{partId}/approve", new { comment = (string?)null, run = true })).EnsureSuccessStatusCode();

    private Task<List<ClashCollision>> CollisionsAsync(Guid partId) =>
        DbAsync(db => db.Set<ClashCollision>().AsNoTracking().Where(c => c.PartId == partId).OrderBy(c => c.Number).ToListAsync());

    private string ImagePath(Guid partId, Guid collisionId)
    {
        using var scope = Server.Services.CreateScope();
        return scope.ServiceProvider.GetRequiredService<ClashStorage>().ImagePath(partId, collisionId);
    }

    private static (int Width, int Height) PngSize(byte[] png)
    {
        png.AsSpan(0, 8).ToArray().ShouldBe(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A });
        System.Text.Encoding.ASCII.GetString(png, 12, 4).ShouldBe("IHDR");
        return (BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(16)), BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(20)));
    }

    [Test]
    public async Task Images_AfterApprovedRun_AllCollisionsGetPng()
    {
        var (admin, agent, partId) = await PartAsync();
        await using var _ = agent;
        await ApproveAsync(admin, partId);
        var (engine, images, _) = Workers();
        (await engine.RunOnceAsync(CancellationToken.None)).ShouldBe(RunOutcome.Succeeded);

        (await images.RunOnceAsync(CancellationToken.None)).ShouldBe(true);

        var collisions = await CollisionsAsync(partId);
        collisions.Count.ShouldBe(2);
        foreach (var collision in collisions)
        {
            collision.HasImage.ShouldBeTrue();
            (collision.ImageFingerprintA, collision.ImageFingerprintB).ShouldBe((collision.FingerprintA, collision.FingerprintB));
            PngSize(await File.ReadAllBytesAsync(ImagePath(partId, collision.Id))).ShouldBe((640, 400));
        }

        (await DbAsync(db => db.Set<ClashImageJob>().CountAsync())).ShouldBe(0);
        (await images.RunOnceAsync(CancellationToken.None)).ShouldBeNull();
    }

    [Test]
    public async Task Images_EngineRunQueued_ReleasesJobBetweenBatches()
    {
        var (admin, agent, partId) = await PartAsync(boxes: 450);
        await using var _ = agent;
        await ApproveAsync(admin, partId);
        var (engine, images, hook) = Workers();
        (await engine.RunOnceAsync(CancellationToken.None)).ShouldBe(RunOutcome.Succeeded);
        (await CollisionsAsync(partId)).Count.ShouldBe(450);
        hook.AfterUpload = async _ =>
        {
            hook.AfterUpload = null;
            (await admin.PostAsync($"/api/clash/parts/{partId}/draft/run", null)).EnsureSuccessStatusCode();
        };

        (await images.RunOnceAsync(CancellationToken.None)).ShouldBe(false);

        hook.Uploads.ShouldBe(1);
        (await CollisionsAsync(partId)).Count(c => c.HasImage).ShouldBe(EngineApi.ImageBatchSize);
        var job = await DbAsync(db => db.Set<ClashImageJob>().AsNoTracking().SingleAsync());
        (job.Attempt, job.LeaseUntil).ShouldBe((0, (DateTimeOffset?)null));
        (await engine.RunOnceAsync(CancellationToken.None)).ShouldBe(RunOutcome.Succeeded); // следующий цикл берёт прогон
        (await images.RunOnceAsync(CancellationToken.None)).ShouldBe(true);
        (await CollisionsAsync(partId)).ShouldAllBe(c => c.HasImage);
    }

    [Test]
    public async Task Images_CancelledByNewerRun_DropsJob()
    {
        var (admin, agent, partId) = await PartAsync();
        await using var _ = agent;
        await ApproveAsync(admin, partId);
        var (engine, images, hook) = Workers();
        (await engine.RunOnceAsync(CancellationToken.None)).ShouldBe(RunOutcome.Succeeded);
        var (other, _, _) = Workers();
        hook.AfterBatch = async _ =>
        {
            hook.AfterBatch = null;
            using (var scope = Server.Services.CreateScope())
                (await scope.ServiceProvider.GetRequiredService<ApprovedRuns>().EnqueueAsync(partId, null, [], Actor.System, CancellationToken.None))
                    .ShouldNotBeNull();
            (await other.RunOnceAsync(CancellationToken.None)).ShouldBe(RunOutcome.Succeeded);
        };

        (await images.RunOnceAsync(CancellationToken.None)).ShouldBe(false);

        var collisions = await CollisionsAsync(partId);
        collisions.ShouldAllBe(c => !c.HasImage && !File.Exists(ImagePath(partId, c.Id)));
        var newer = collisions[0].LastFoundRunId;
        (await DbAsync(db => db.Set<ClashImageJob>().AsNoTracking().SingleAsync())).RunId.ShouldBe(newer);
        (await images.RunOnceAsync(CancellationToken.None)).ShouldBe(true);
        (await CollisionsAsync(partId)).ShouldAllBe(c => c.HasImage);
    }
}
