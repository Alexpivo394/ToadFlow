using System.Collections.Concurrent;
using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Shouldly;
using ToadFlow.Contracts;
using ToadFlow.Contracts.Clash;
using ToadFlow.Server.Clash.Engine;
using ToadFlow.Server.Tests.Infrastructure;

namespace ToadFlow.Clash.Worker.Tests;

/// <summary>Работник в протоколе: ход идёт таймером в каждой фазе, ошибка сервера при сдаче называется своим именем.</summary>
public class EngineWorkerProtocolTests : ServerTest
{
    private readonly string _cache = Path.Combine(Path.GetTempPath(), "toadflow-engine-cache-tests", Guid.NewGuid().ToString("N"));
    private readonly FakeTimeProvider _time = new(DateTimeOffset.UtcNow);

    [After(Test)]
    public void CleanCache()
    {
        if (Directory.Exists(_cache)) Directory.Delete(_cache, true);
    }

    /// <summary>Прокладка между работником и сервером: пишет запросы, держит или подменяет ответы выбранных путей.</summary>
    private sealed class Spy(HttpMessageHandler inner) : DelegatingHandler(inner)
    {
        public ConcurrentQueue<string> Paths { get; } = new();
        public Func<string, Task>? Hold { get; set; }
        public Func<string, HttpResponseMessage?>? Respond { get; set; }

        public int Count(string suffix) => Paths.Count(p => p.EndsWith(suffix, StringComparison.Ordinal));

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var path = request.RequestUri!.AbsolutePath;
            Paths.Enqueue(path);
            if (Hold is { } hold) await hold(path).WaitAsync(ct);
            return Respond?.Invoke(path) ?? await base.SendAsync(request, ct);
        }
    }

    private (EngineWorker Worker, EngineClient Client, Spy Spy) Worker()
    {
        var spy = new Spy(Server.Server.CreateHandler());
        var http = new HttpClient(spy) { BaseAddress = new Uri("http://localhost/") };
        http.DefaultRequestHeaders.Add(EngineApi.KeyHeader, ServerFactory.EngineKey);
        var client = new EngineClient(http);
        var options = new WorkerOptions { CacheRoot = _cache, Threads = 2, ProgressInterval = TimeSpan.FromSeconds(20) };
        return (new EngineWorker(client, new SnapshotCache(client, options), options, NullLogger<EngineWorker>.Instance, _time), client, spy);
    }

    private async Task<(HttpClient Admin, Guid PartId)> PartAsync()
    {
        var admin = await AdminAsync();
        var agent = await FakeAgent.RegisterAsync(Server, admin);
        await agent.ConnectAsync("1.8.0", new InstalledRevitDto(2022, true));
        return (admin, await ClashDraftFixtures.PartAsync(Server, admin, agent, "POL_K1_AR.rvt", "POL_K1_OV.rvt"));
    }

    private Task<EngineRun> LastRunAsync() => DbAsync(db => db.Set<EngineRun>().AsNoTracking().OrderByDescending(r => r.CreatedAt).FirstAsync());

    private static async Task WaitAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(60);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("Условие не наступило за 60 с");
            await Task.Delay(10);
        }
    }

    /// <summary>
    ///     Долгий <c>BasisAsync</c> (скачивание прошлых слепков) и долгая сдача результата: ход уходит каждые 20 с времени работника,
    ///     а не только вокруг поиска. Тот же таймер идёт и во время сверки с прошлым прогоном.
    /// </summary>
    [Test]
    public async Task Worker_HeartbeatDuringBasisAndCompare()
    {
        var (admin, partId) = await PartAsync();
        (await admin.PostJsonAsync($"/api/clash/parts/{partId}/approve", new { comment = (string?)null, run = true })).EnsureSuccessStatusCode();
        var (worker, client, spy) = Worker();
        var assignment = (await client.ClaimAsync(CancellationToken.None)).ShouldNotBeNull();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        spy.Hold = path => path.EndsWith("/basis", StringComparison.Ordinal) || path.EndsWith("/result", StringComparison.Ordinal) ? release.Task : Task.CompletedTask;

        var run = worker.ProcessAsync(assignment, CancellationToken.None);
        await WaitAsync(() => spy.Count("/basis") == 1);
        var before = spy.Count("/progress");
        for (var beat = 1; beat <= 3; beat++)
        {
            _time.Advance(TimeSpan.FromSeconds(20));
            await WaitAsync(() => spy.Count("/progress") >= before + beat);
        }

        release.SetResult();
        (await run).ShouldBe(RunOutcome.Succeeded);
        (await LastRunAsync()).State.ShouldBe(EngineRunState.Succeeded);
    }

    [Test]
    public async Task Worker_HeartbeatNotFasterThanInterval()
    {
        var (admin, partId) = await PartAsync();
        (await admin.PostJsonAsync($"/api/clash/parts/{partId}/approve", new { comment = (string?)null, run = true })).EnsureSuccessStatusCode();
        var (worker, client, spy) = Worker();
        var assignment = (await client.ClaimAsync(CancellationToken.None)).ShouldNotBeNull();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        spy.Hold = path => path.EndsWith("/basis", StringComparison.Ordinal) ? release.Task : Task.CompletedTask;

        var run = worker.ProcessAsync(assignment, CancellationToken.None);
        await WaitAsync(() => spy.Count("/basis") == 1);
        var before = spy.Count("/progress");
        _time.Advance(TimeSpan.FromSeconds(19));
        await Task.Delay(300);

        spy.Count("/progress").ShouldBe(before);
        release.SetResult();
        (await run).ShouldBe(RunOutcome.Succeeded);
    }

    [Test]
    public async Task Worker_HeartbeatGets409_StopsRun()
    {
        var (admin, partId) = await PartAsync();
        (await admin.PostJsonAsync($"/api/clash/parts/{partId}/approve", new { comment = (string?)null, run = true })).EnsureSuccessStatusCode();
        var (worker, client, spy) = Worker();
        var assignment = (await client.ClaimAsync(CancellationToken.None)).ShouldNotBeNull();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        spy.Hold = async path =>
        {
            if (path.EndsWith("/basis", StringComparison.Ordinal)) await release.Task;
        };

        var run = worker.ProcessAsync(assignment, CancellationToken.None);
        await WaitAsync(() => spy.Count("/basis") == 1);
        (await admin.PostAsync($"/api/clash/parts/{partId}/approved/run/cancel", null)).EnsureSuccessStatusCode();
        _time.Advance(TimeSpan.FromSeconds(20));

        (await run.WaitAsync(TimeSpan.FromSeconds(60))).ShouldBe(RunOutcome.Cancelled);
    }

    [Test]
    public async Task Worker_Result500_ErrorTextFromServer_NoSnapshotsMessage()
    {
        var (admin, partId) = await PartAsync();
        (await admin.PostAsync($"/api/clash/parts/{partId}/draft/run", null)).EnsureSuccessStatusCode();
        var (worker, _, spy) = Worker();
        spy.Respond = path => path.EndsWith("/result", StringComparison.Ordinal)
            ? new HttpResponseMessage(HttpStatusCode.InternalServerError) { Content = new StringContent("ошибка сверки: дубль ключа") }
            : null;

        (await worker.RunOnceAsync(CancellationToken.None)).ShouldBe(RunOutcome.Failed);

        var run = await LastRunAsync();
        run.State.ShouldBe(EngineRunState.Failed); // детерминированная ошибка сервера: второй пересчёт не нужен
        run.Error.ShouldNotBeNull().ShouldContain("ошибка сверки: дубль ключа");
        run.Error.ShouldContain("500");
        run.Error.ShouldNotContain("слепки");
        spy.Count("/result").ShouldBe(1);
    }

    [Test]
    public async Task Worker_Result4xx_NoRetry()
    {
        var (admin, partId) = await PartAsync();
        (await admin.PostAsync($"/api/clash/parts/{partId}/draft/run", null)).EnsureSuccessStatusCode();
        var (worker, _, spy) = Worker();
        spy.Respond = path => path.EndsWith("/result", StringComparison.Ordinal)
            ? new HttpResponseMessage(HttpStatusCode.BadRequest) { Content = new StringContent("Часть результата — до 50000 коллизий") }
            : null;

        (await worker.RunOnceAsync(CancellationToken.None)).ShouldBe(RunOutcome.Failed);

        var run = await LastRunAsync();
        run.State.ShouldBe(EngineRunState.Failed);
        run.Attempt.ShouldBe(1);
        run.Error.ShouldNotBeNull().ShouldContain("Часть результата");
        spy.Count("/result").ShouldBe(1);
    }

    [Test]
    public async Task Worker_Result503_RetriedOnNextAttempt()
    {
        var (admin, partId) = await PartAsync();
        (await admin.PostAsync($"/api/clash/parts/{partId}/draft/run", null)).EnsureSuccessStatusCode();
        var (worker, _, spy) = Worker();
        spy.Respond = path => path.EndsWith("/result", StringComparison.Ordinal)
            ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable) { Content = new StringContent("перезапуск") }
            : null;

        (await worker.RunOnceAsync(CancellationToken.None)).ShouldBe(RunOutcome.Failed);

        var run = await LastRunAsync();
        run.State.ShouldBe(EngineRunState.Queued); // шлюз недоступен: попытка может пройти
        run.Error.ShouldNotBeNull().ShouldContain("перезапуск");
    }
}
