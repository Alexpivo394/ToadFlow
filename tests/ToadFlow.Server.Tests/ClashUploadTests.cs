using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using Microsoft.EntityFrameworkCore;
using Shouldly;
using ToadFlow.Contracts;
using ToadFlow.Contracts.Clash;
using ToadFlow.Server.Clash;
using ToadFlow.Server.Data.Entities;
using ToadFlow.Server.Tests.Infrastructure;

namespace ToadFlow.Server.Tests;

public class ClashUploadTests : ServerTest
{
    private const string Uploads = "/api/agent/clash/uploads";

    private async Task<FakeAgent> AgentAsync(string name = "FARM-01")
    {
        var agent = await FakeAgent.RegisterAsync(Server, await AdminAsync(), name);
        await agent.ConnectAsync("1.8.0", new InstalledRevitDto(2022, true));
        return agent;
    }

    private static async Task<ClashUploadState> StartAsync(HttpClient client, ClashFiles files, Guid jobId)
    {
        var response = await client.PostJsonAsync(Uploads, files.Request(jobId));
        response.StatusCode.ShouldBe(HttpStatusCode.Created);
        return await response.ReadJsonAsync<ClashUploadState>();
    }

    private static Task<HttpResponseMessage> PutAsync(HttpClient client, Guid uploadId, string file, long offset, byte[] chunk, bool gzip = false)
    {
        HttpContent content;
        if (gzip)
        {
            using var packed = new MemoryStream();
            using (var zip = new GZipStream(packed, CompressionLevel.Fastest, true)) zip.Write(chunk);
            content = new ByteArrayContent(packed.ToArray());
            content.Headers.ContentEncoding.Add("gzip");
        }
        else content = new ByteArrayContent(chunk);

        content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        return client.PutAsync($"{Uploads}/{uploadId}/{file}?offset={offset}", content);
    }

    private static async Task SendAllAsync(HttpClient client, Guid uploadId, ClashFiles files)
    {
        (await PutAsync(client, uploadId, ClashUploads.Geometry, 0, files.Geometry)).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await PutAsync(client, uploadId, ClashUploads.Parameters, 0, files.Parameters)).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    private static Task<HttpResponseMessage> CompleteAsync(HttpClient client, Guid uploadId) => client.PostAsync($"{Uploads}/{uploadId}/complete", null);

    private Task<List<ClashSnapshot>> SnapshotsAsync() => DbAsync(db => db.Set<ClashSnapshot>().AsNoTracking().ToListAsync());

    private string UploadsFolder => Path.Combine(Server.ClashStorageRoot, "uploads");

    [Test]
    public async Task Upload_HappyPath_PublishesSnapshotWithCatalog()
    {
        await using var agent = await AgentAsync();
        var (jobId, model) = await ClashFixtures.RunningJobAsync(Server, agent);
        var files = ClashFixtures.ModelFiles(model.SnapshotKey, padding: 50);
        var client = ClashFixtures.AgentClient(Server, agent);
        var upload = await StartAsync(client, files, jobId);

        var third = files.Geometry.Length / 3;
        (await PutAsync(client, upload.UploadId, ClashUploads.Geometry, 0, files.Geometry[..third])).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await PutAsync(client, upload.UploadId, ClashUploads.Geometry, third, files.Geometry[third..(2 * third)])).StatusCode.ShouldBe(HttpStatusCode.OK);
        var last = await PutAsync(client, upload.UploadId, ClashUploads.Geometry, 2 * third, files.Geometry[(2 * third)..], gzip: true);
        last.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await last.ReadJsonAsync<ClashUploadState>()).GeometryReceived.ShouldBe(files.Geometry.Length);
        (await PutAsync(client, upload.UploadId, ClashUploads.Parameters, 0, files.Parameters)).StatusCode.ShouldBe(HttpStatusCode.OK);

        var response = await CompleteAsync(client, upload.UploadId);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var result = await response.ReadJsonAsync<ClashExtractResult>();
        result.Elements.ShouldBe(files.Elements);
        result.Triangles.ShouldBe(files.Triangles);
        result.Inventory.ShouldBe(files.Inventory);
        result.GeometryBytes.ShouldBe(files.Geometry.Length);
        var snapshot = (await SnapshotsAsync()).ShouldHaveSingleItem();
        snapshot.Id.ShouldBe(result.SnapshotId);
        snapshot.ModelId.ShouldBe(model.Id);
        snapshot.JobId.ShouldBe(jobId);
        snapshot.ViewName.ShouldBe("Navisworks");
        snapshot.Catalog.Categories.Select(c => c.Key).ShouldBe(["OST_GenericModel", "OST_PipeCurves", "OST_Walls"]);
        var folder = Path.Combine(Server.ClashStorageRoot, model.Id.ToString("N"), snapshot.Id.ToString("N"));
        File.ReadAllBytes(Path.Combine(folder, "model.tcg")).ShouldBe(files.Geometry);
        File.ReadAllBytes(Path.Combine(folder, "model.tcp")).ShouldBe(files.Parameters);
        Directory.Exists(Path.Combine(UploadsFolder, upload.UploadId.ToString("N"))).ShouldBeFalse();

        var again = await CompleteAsync(client, upload.UploadId);
        again.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await again.ReadJsonAsync<ClashExtractResult>()).SnapshotId.ShouldBe(result.SnapshotId);
    }

    [Test]
    public async Task Upload_ResumesFromReceivedLength()
    {
        await using var agent = await AgentAsync();
        var (jobId, model) = await ClashFixtures.RunningJobAsync(Server, agent);
        var files = ClashFixtures.ModelFiles(model.SnapshotKey, padding: 20);
        var client = ClashFixtures.AgentClient(Server, agent);
        var upload = await StartAsync(client, files, jobId);
        var half = files.Geometry.Length / 2;
        await PutAsync(client, upload.UploadId, ClashUploads.Geometry, 0, files.Geometry[..half]);

        // Ответ на часть потерялся, агент шлёт её снова.
        var repeated = await PutAsync(client, upload.UploadId, ClashUploads.Geometry, 0, files.Geometry[..half]);

        repeated.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await repeated.ReadJsonAsync<ClashUploadState>()).GeometryReceived.ShouldBe(half);
        (await client.GetJsonAsync<ClashUploadState>($"{Uploads}/{upload.UploadId}")).GeometryReceived.ShouldBe(half);
        (await PutAsync(client, upload.UploadId, ClashUploads.Geometry, half, files.Geometry[half..])).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await PutAsync(client, upload.UploadId, ClashUploads.Parameters, 0, files.Parameters)).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await CompleteAsync(client, upload.UploadId)).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await SnapshotsAsync()).Count.ShouldBe(1);
    }

    [Test]
    [Arguments("incomplete")]
    [Arguments("corrupt")]
    [Arguments("foreign")]
    public async Task Complete_RejectsIncompleteOrCorrupt(string defect)
    {
        await using var agent = await AgentAsync();
        var (jobId, model) = await ClashFixtures.RunningJobAsync(Server, agent);
        var files = defect switch
        {
            "incomplete" => ClashFixtures.ModelFiles(model.SnapshotKey, complete: false),
            "foreign" => ClashFixtures.ModelFiles(Guid.NewGuid().ToString("N")),
            _ => ClashFixtures.ModelFiles(model.SnapshotKey)
        };
        var client = ClashFixtures.AgentClient(Server, agent);
        var upload = await StartAsync(client, files, jobId);
        var geometry = files.Geometry.ToArray();
        if (defect == "corrupt") geometry[^5] ^= 0xFF;
        (await PutAsync(client, upload.UploadId, ClashUploads.Geometry, 0, geometry)).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await PutAsync(client, upload.UploadId, ClashUploads.Parameters, 0, files.Parameters)).StatusCode.ShouldBe(HttpStatusCode.OK);

        var response = await CompleteAsync(client, upload.UploadId);

        response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        var text = await response.Content.ReadAsStringAsync();
        text.ShouldContain(defect switch
        {
            "incomplete" => "выгрузка модели не завершена",
            "foreign" => "слепок другой модели",
            _ => "контрольная сумма"
        });
        (await SnapshotsAsync()).ShouldBeEmpty();
        Directory.Exists(Path.Combine(UploadsFolder, upload.UploadId.ToString("N"))).ShouldBeFalse();
    }

    [Test]
    public async Task Upload_RejectedWhenJobNotRunningOnThisAgent()
    {
        await using var owner = await AgentAsync("FARM-01");
        await using var stranger = await AgentAsync("FARM-02");
        var (jobId, model) = await ClashFixtures.RunningJobAsync(Server, owner);
        var files = ClashFixtures.ModelFiles(model.SnapshotKey);

        (await ClashFixtures.AgentClient(Server, stranger).PostJsonAsync(Uploads, files.Request(jobId))).StatusCode.ShouldBe(HttpStatusCode.Conflict);

        var client = ClashFixtures.AgentClient(Server, owner);
        var upload = await StartAsync(client, files, jobId);
        await SendAllAsync(client, upload.UploadId, files);
        await DbAsync(db => db.Jobs.Where(j => j.Id == jobId).ExecuteUpdateAsync(j => j.SetProperty(x => x.Status, JobStatus.Succeeded)));
        (await CompleteAsync(client, upload.UploadId)).StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await SnapshotsAsync()).ShouldBeEmpty();

        var exportJob = await DbAsync(async db =>
        {
            var job = Seed.Job(db, Seed.Run(db).Id);
            await db.SaveChangesAsync();
            return job.Id;
        });
        (await owner.RequestJobAsync(2, 2022)).ShouldNotBeNull().JobId.ShouldBe(exportJob);
        (await client.PostJsonAsync(Uploads, files.Request(exportJob))).StatusCode.ShouldBe(HttpStatusCode.Conflict);
    }

    [Test]
    public async Task Upload_ChunkOverLimit_Returns413()
    {
        await using var agent = await AgentAsync();
        var (jobId, model) = await ClashFixtures.RunningJobAsync(Server, agent);
        var files = ClashFixtures.ModelFiles(model.SnapshotKey);
        var client = ClashFixtures.AgentClient(Server, agent);
        var request = files.Request(jobId) with { GeometryBytes = 20L * 1024 * 1024 };
        var upload = await (await client.PostJsonAsync(Uploads, request)).ReadJsonAsync<ClashUploadState>();

        var response = await PutAsync(client, upload.UploadId, ClashUploads.Geometry, 0, new byte[ClashUploads.MaxChunkBytes + 1], gzip: true);

        response.StatusCode.ShouldBe(HttpStatusCode.RequestEntityTooLarge);
        (await client.GetJsonAsync<ClashUploadState>($"{Uploads}/{upload.UploadId}")).GeometryReceived.ShouldBe(0);
    }

    [Test]
    public async Task Upload_BeyondDeclaredSize_Returns422()
    {
        await using var agent = await AgentAsync();
        var (jobId, model) = await ClashFixtures.RunningJobAsync(Server, agent);
        var files = ClashFixtures.ModelFiles(model.SnapshotKey);
        var client = ClashFixtures.AgentClient(Server, agent);
        var upload = await StartAsync(client, files, jobId);

        var response = await PutAsync(client, upload.UploadId, ClashUploads.Parameters, 0, [.. files.Parameters, 1, 2, 3]);

        response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        (await client.GetJsonAsync<ClashUploadState>($"{Uploads}/{upload.UploadId}")).ParametersReceived.ShouldBe(0);
    }

    [Test]
    public async Task Upload_WithUserToken_Returns401()
    {
        await using var agent = await AgentAsync();
        var (jobId, model) = await ClashFixtures.RunningJobAsync(Server, agent);
        var admin = await AdminAsync();

        var response = await admin.PostJsonAsync(Uploads, ClashFixtures.ModelFiles(model.SnapshotKey).Request(jobId));

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Test]
    public async Task Publish_KeepsLastNSnapshots()
    {
        await DbAsync(db => new ClashSettingsStore(db).SaveStorageAsync(new ClashStorageSettings(2)));
        await using var agent = await AgentAsync();
        var client = ClashFixtures.AgentClient(Server, agent);
        var published = new List<Guid>();
        ClashModel? model = null;
        for (var i = 0; i < 3; i++)
        {
            var (jobId, jobModel) = await ClashFixtures.RunningJobAsync(Server, agent);
            model = jobModel;
            var files = ClashFixtures.ModelFiles(jobModel.SnapshotKey, extractedAt: DateTimeOffset.UtcNow.AddHours(i - 3));
            var upload = await StartAsync(client, files, jobId);
            await SendAllAsync(client, upload.UploadId, files);
            published.Add((await (await CompleteAsync(client, upload.UploadId)).ReadJsonAsync<ClashExtractResult>()).SnapshotId);
            await agent.CompleteJobAsync(jobId, JobOutcome.Succeeded);
        }

        var kept = await SnapshotsAsync();
        kept.Select(s => s.Id).Order().ShouldBe(published.Skip(1).Order());
        var modelFolder = Path.Combine(Server.ClashStorageRoot, model!.Id.ToString("N"));
        Directory.GetDirectories(modelFolder).Select(Path.GetFileName).Order().ShouldBe(published.Skip(1).Select(id => id.ToString("N")).Order());
    }
}
