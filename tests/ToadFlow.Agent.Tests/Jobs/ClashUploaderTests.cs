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

    /// <summary>Журнал задания бросает (обрыв хаба, ошибка сервера) — загрузка и итог от этого не страдают.</summary>
    private sealed class ThrowingReporter(Exception error) : IJobReporter
    {
        public Task ProgressAsync(JobProgressDto progress) => Task.FromException(error);

        public Task InstanceKilledAsync(int slot, string reason) => Task.CompletedTask;
    }

    /// <summary>Первые <paramref name="failures" /> запросов не доходят: сервер лежит (или <paramref name="error" /> — свой сбой).</summary>
    private sealed class FailingHandler(HttpMessageHandler inner, int failures, Func<Exception>? error = null) : DelegatingHandler(inner)
    {
        private int _calls;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Interlocked.Increment(ref _calls) <= failures
                ? Task.FromException<HttpResponseMessage>(error?.Invoke() ?? new HttpRequestException("сервер недоступен"))
                : base.SendAsync(request, ct);
    }

    private ClashUploader Uploader(FakeAgent agent, Func<HttpMessageHandler> handler, TimeSpan budget) =>
        new(new AgentConfig("http://localhost", agent.Id, agent.Key), new AgentOptions { HttpMessageHandlerFactory = handler }, NullLogger<ClashUploader>.Instance,
            NoDelays, budget);

    [Test]
    [Arguments(typeof(TimeoutException))]
    [Arguments(typeof(Microsoft.AspNetCore.SignalR.HubException))]
    public async Task UploadLogFailure_DoesNotAbortJob(Type error)
    {
        await using var agent = await AgentAsync();
        var (jobId, model) = await ClashFixtures.RunningJobAsync(Server, agent);
        var reporter = new ThrowingReporter((Exception)Activator.CreateInstance(error, "журнал не дошёл")!);

        var completion = await Uploader(agent).FinishAsync(Assignment(jobId), 1, Staged(jobId, ClashFixtures.ModelFiles(model.SnapshotKey)), reporter,
            CancellationToken.None);

        completion.Outcome.ShouldBe(JobOutcome.Succeeded);
        (await DbAsync(db => db.Set<ClashSnapshot>().CountAsync())).ShouldBe(1);
    }

    /// <summary>Сервер лежит дольше прежних пяти повторов (перезапуск с миграциями): загрузка ждёт в пределах бюджета и доходит.</summary>
    [Test]
    public async Task ServerDown10Min_UploadResumesAndSucceeds()
    {
        await using var agent = await AgentAsync();
        var (jobId, model) = await ClashFixtures.RunningJobAsync(Server, agent);

        var uploader = Uploader(agent, () => new FailingHandler(Server.Server.CreateHandler(), failures: 12), TimeSpan.FromMinutes(30));
        var completion = await uploader.FinishAsync(Assignment(jobId), 1, Staged(jobId, ClashFixtures.ModelFiles(model.SnapshotKey)), new Reporter(),
            CancellationToken.None);

        completion.Outcome.ShouldBe(JobOutcome.Succeeded);
    }

    [Test]
    public async Task HttpClientTimeout_IsRetried()
    {
        await using var agent = await AgentAsync();
        var (jobId, model) = await ClashFixtures.RunningJobAsync(Server, agent);

        var uploader = Uploader(agent, () => new FailingHandler(Server.Server.CreateHandler(), failures: 1,
            () => new TaskCanceledException("тайм-аут запроса", new TimeoutException())), TimeSpan.FromMinutes(30));
        var completion = await uploader.FinishAsync(Assignment(jobId), 1, Staged(jobId, ClashFixtures.ModelFiles(model.SnapshotKey)), new Reporter(),
            CancellationToken.None);

        completion.Outcome.ShouldBe(JobOutcome.Succeeded);
    }

    /// <summary>Загрузка так и не дошла: слепок не выбрасывается, следующая попытка того же задания загружает его без Revit.</summary>
    [Test]
    public async Task UploadFailedFinally_SnapshotFolderKeptForSameJobRetry()
    {
        await using var agent = await AgentAsync();
        var (jobId, model) = await ClashFixtures.RunningJobAsync(Server, agent);
        var files = ClashFixtures.ModelFiles(model.SnapshotKey);

        var down = Uploader(agent, () => new FailingHandler(Server.Server.CreateHandler(), failures: int.MaxValue), TimeSpan.Zero);
        var failed = await down.FinishAsync(Assignment(jobId), 1, Staged(jobId, files), new Reporter(), CancellationToken.None);

        failed.Outcome.ShouldBe(JobOutcome.InfrastructureFailure);
        Directory.Exists(ClashStaging.Folder(jobId, 1)).ShouldBeFalse();
        Directory.Exists(ClashStaging.Kept(jobId)).ShouldBeTrue();

        var retry = Assignment(jobId) with { Attempt = 2 };
        var kept = Uploader(agent).TakeKept(retry).ShouldNotBeNull();
        kept.Outcome.ShouldBe(JobOutcome.Succeeded);
        Directory.Exists(ClashStaging.Kept(jobId)).ShouldBeFalse();
        Directory.Exists(ClashStaging.Folder(jobId, 2)).ShouldBeTrue();
        // Сервер ведёт задание на попытке 1: сдаём от её имени — проверка здесь только в том, что слепок цел и загружается.
        var completion = await Uploader(agent).FinishAsync(Assignment(jobId), 1, kept, new Reporter(), CancellationToken.None);
        completion.Outcome.ShouldBe(JobOutcome.Succeeded);
        Uploader(agent).TakeKept(retry).ShouldBeNull();
    }
}
