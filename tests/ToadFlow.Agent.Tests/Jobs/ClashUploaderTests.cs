using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using ToadFlow.Agent.Clash;
using ToadFlow.Agent.Config;
using ToadFlow.Agent.Jobs;
using ToadFlow.Agent.Server;
using ToadFlow.Contracts;
using ToadFlow.Contracts.Clash;
using ToadFlow.Server.Clash;
using ToadFlow.Server.Tests.Infrastructure;

namespace ToadFlow.Agent.Tests.Jobs;

public class ClashUploaderTests : ServerTest
{
    private sealed class Reporter : IJobReporter
    {
        public ConcurrentQueue<JobLogDto> Lines { get; } = new();

        public Task ProgressAsync(JobProgressDto progress)
        {
            foreach (var line in progress.Lines) Lines.Enqueue(line);
            return Task.CompletedTask;
        }

        public Task InstanceKilledAsync(int slot, string reason) => Task.CompletedTask;
    }

    /// <summary>Рвёт заданный по счёту запрос PUT после того, как сервер его принял: ответ до агента не доходит.</summary>
    private sealed class DroppingHandler(HttpMessageHandler inner, int dropPut) : DelegatingHandler(inner)
    {
        private int _puts;

        public int Puts => _puts;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var response = await base.SendAsync(request, ct);
            if (request.Method == HttpMethod.Put && Interlocked.Increment(ref _puts) == dropPut) throw new HttpRequestException("связь оборвалась");
            return response;
        }
    }

    private static readonly TimeSpan[] NoDelays = [TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero];

    private async Task<FakeAgent> AgentAsync()
    {
        var agent = await FakeAgent.RegisterAsync(Server, await AdminAsync());
        await agent.ConnectAsync("1.8.0", new InstalledRevitDto(2022, true));
        return agent;
    }

    private ClashUploader Uploader(FakeAgent agent, Func<HttpMessageHandler>? handler = null) =>
        new(new AgentConfig("http://localhost", agent.Id, agent.Key), new AgentOptions { HttpMessageHandlerFactory = handler ?? (() => Server.Server.CreateHandler()) },
            NullLogger<ClashUploader>.Instance, NoDelays);

    private static JobAssignmentDto Assignment(Guid jobId, string kind = ModuleKinds.ClashExtract) => new(jobId, kind, "АР.rvt", "{}", 60, 1, 2);

    /// <summary>Слепок там, куда его кладёт аддин, и ответ аддина агенту.</summary>
    private static JobCompletionDto Staged(Guid jobId, ClashFiles files, string? folder = null)
    {
        folder ??= ClashStaging.Folder(jobId, 1);
        Directory.CreateDirectory(folder);
        File.WriteAllBytes(Path.Combine(folder, "model.tcg"), files.Geometry);
        File.WriteAllBytes(Path.Combine(folder, "model.tcp"), files.Parameters);
        var staged = new ClashStagedSnapshot(folder, files.Elements, files.Triangles, files.Inventory);
        return new JobCompletionDto(jobId, JobOutcome.Succeeded, null, JsonSerializer.Serialize(staged, TestJson.Options));
    }

    [Test]
    public async Task Uploader_UploadsAndReplacesResult()
    {
        await using var agent = await AgentAsync();
        var (jobId, model) = await ClashFixtures.RunningJobAsync(Server, agent);
        var files = ClashFixtures.ModelFiles(model.SnapshotKey, padding: 80_000); // больше двух частей по 8 МиБ
        files.Geometry.Length.ShouldBeGreaterThan(2 * ClashUploads.MaxChunkBytes);
        var reporter = new Reporter();

        var completion = await Uploader(agent).FinishAsync(Assignment(jobId), 1, Staged(jobId, files), reporter, CancellationToken.None);

        completion.Outcome.ShouldBe(JobOutcome.Succeeded);
        var result = JsonSerializer.Deserialize<ClashExtractResult>(completion.ResultJson!, TestJson.Options)!;
        result.Elements.ShouldBe(files.Elements);
        result.GeometryBytes.ShouldBe(files.Geometry.Length);
        (await DbAsync(db => db.Set<ClashSnapshot>().SingleAsync())).Id.ShouldBe(result.SnapshotId);
        Directory.Exists(ClashStaging.Folder(jobId, 1)).ShouldBeFalse();
        reporter.Lines.ShouldContain(line => line.Text.Contains("слепок загружен"));
    }

    [Test]
    public async Task Uploader_ResumesAfterDroppedChunk()
    {
        await using var agent = await AgentAsync();
        var (jobId, model) = await ClashFixtures.RunningJobAsync(Server, agent);
        var files = ClashFixtures.ModelFiles(model.SnapshotKey, padding: 80_000);
        DroppingHandler? dropping = null;
        var uploader = Uploader(agent, () => dropping = new DroppingHandler(Server.Server.CreateHandler(), dropPut: 2));

        var completion = await uploader.FinishAsync(Assignment(jobId), 1, Staged(jobId, files), new Reporter(), CancellationToken.None);

        completion.Outcome.ShouldBe(JobOutcome.Succeeded);
        var snapshot = await DbAsync(db => db.Set<ClashSnapshot>().SingleAsync());
        snapshot.GeometryBytes.ShouldBe(files.Geometry.Length);
        // 3 части геометрии + 1 параметров; оборванная вторая часть сервером уже принята и не отправляется снова.
        dropping.ShouldNotBeNull().Puts.ShouldBe(4);
    }

    [Test]
    public async Task Uploader_ServerRejects_Fails()
    {
        await using var agent = await AgentAsync();
        var (jobId, _) = await ClashFixtures.RunningJobAsync(Server, agent);
        var files = ClashFixtures.ModelFiles(Guid.NewGuid().ToString("N")); // слепок чужой модели

        var completion = await Uploader(agent).FinishAsync(Assignment(jobId), 1, Staged(jobId, files), new Reporter(), CancellationToken.None);

        completion.Outcome.ShouldBe(JobOutcome.Failed);
        completion.Note.ShouldNotBeNull().ShouldContain("слепок другой модели");
        Directory.Exists(ClashStaging.Folder(jobId, 1)).ShouldBeFalse();
    }

    [Test]
    public async Task Uploader_PathOutsideStaging_Fails()
    {
        await using var agent = await AgentAsync();
        var (jobId, model) = await ClashFixtures.RunningJobAsync(Server, agent);
        var outside = Path.Combine(Path.GetTempPath(), "clash-outside-" + Guid.NewGuid().ToString("N"));

        var completion = await Uploader(agent).FinishAsync(Assignment(jobId), 1, Staged(jobId, ClashFixtures.ModelFiles(model.SnapshotKey), outside),
            new Reporter(), CancellationToken.None);

        completion.Outcome.ShouldBe(JobOutcome.Failed);
        completion.Note.ShouldNotBeNull().ShouldContain("вне рабочей папки");
        Directory.Exists(outside).ShouldBeTrue(); // чужую папку агент не трогает
        Directory.Delete(outside, true);
    }

    [Test]
    public async Task Uploader_NonClashJob_Untouched()
    {
        await using var agent = await AgentAsync();
        var original = new JobCompletionDto(Guid.NewGuid(), JobOutcome.Succeeded, "готово", """{"rvt":"Done"}""");

        var completion = await Uploader(agent).FinishAsync(Assignment(original.JobId, ModuleKinds.Export), 1, original, new Reporter(), CancellationToken.None);

        completion.ShouldBe(original);
    }

    [Test]
    public async Task Uploader_FailedExtraction_RemovesStagingWithoutUpload()
    {
        await using var agent = await AgentAsync();
        var jobId = Guid.NewGuid();
        var staged = Staged(jobId, ClashFixtures.ModelFiles("k"));
        var failed = staged with { Outcome = JobOutcome.Failed, Note = "в модели нет 3D-вида «Navisworks»" };

        var completion = await Uploader(agent).FinishAsync(Assignment(jobId), 1, failed, new Reporter(), CancellationToken.None);

        completion.ShouldBe(failed);
        Directory.Exists(ClashStaging.Folder(jobId, 1)).ShouldBeFalse();
    }

    [Test]
    public async Task Upload_ExtendsJobLease()
    {
        await using var agent = await AgentAsync();
        var (jobId, model) = await ClashFixtures.RunningJobAsync(Server, agent);
        await DbAsync(db => db.Jobs.Where(j => j.Id == jobId).ExecuteUpdateAsync(j => j.SetProperty(x => x.LeaseUntil, DateTimeOffset.UtcNow.AddSeconds(5))));

        await Uploader(agent).FinishAsync(Assignment(jobId), 1, Staged(jobId, ClashFixtures.ModelFiles(model.SnapshotKey)), new Reporter(), CancellationToken.None);

        var job = await DbAsync(db => db.Jobs.AsNoTracking().SingleAsync(j => j.Id == jobId));
        job.LeaseUntil.ShouldNotBeNull().ShouldBeGreaterThan(DateTimeOffset.UtcNow.AddMinutes(1));
    }
}
