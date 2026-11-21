using System.IO.Compression;
using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using ToadFlow.Agent.Config;
using ToadFlow.Agent.Families;
using ToadFlow.Agent.Server;
using ToadFlow.Contracts;
using ToadFlow.Contracts.Families;

namespace ToadFlow.Agent.Tests.Families;

public class FamilyLibraryScanTests
{
    private const string SampleName = "Образец 2022.rfa";
    private static readonly string Sample = Path.Combine(AppContext.BaseDirectory, "Families", "samples", SampleName);
    private static readonly TimeSpan[] NoDelays = [TimeSpan.Zero];

    private readonly string _root = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "toadflow-lib-" + Guid.NewGuid().ToString("N"))).FullName;
    private readonly string _data = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "toadflow-data-" + Guid.NewGuid().ToString("N"))).FullName;

    [After(Test)]
    public void Cleanup()
    {
        foreach (var folder in new[] { _root, _data })
        {
            try
            {
                Directory.Delete(folder, true);
            }
            catch (IOException)
            {
                // временная папка
            }
        }
    }

    /// <summary>Семейство в библиотеке, записанное давно: обход берёт его сразу.</summary>
    private string Put(string relative, string? source = null)
    {
        var path = Path.Combine(_root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        if (source is null) File.WriteAllText(path, "rfa " + relative);
        else File.Copy(source, path, true);
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(-10));
        return path;
    }

    private static FamilyScanRequestDto Request(string root) => new(Guid.NewGuid(), root, "_На проверке");

    private async Task<FamilyScanReportDto> ScanAsync(string? root = null)
    {
        var reports = new List<FamilyScanReportDto>();
        using var scan = new FamilyLibraryScan((report, _) =>
        {
            reports.Add(report);
            return Task.CompletedTask;
        }, _data, NullLogger<FamilyLibraryScan>.Instance);
        scan.Start(Request(root ?? _root)).ShouldBeTrue();
        await scan.Current!;
        return reports.ShouldHaveSingleItem();
    }

    [Test]
    public async Task Scan_ReportsFilesAndKeepsHistory()
    {
        Put(@"ОВ\Клапаны\Клапан.rfa");
        Put(@"ОВ\Клапаны\Клапан.txt");

        var report = await ScanAsync();

        report.Complete.ShouldBeTrue();
        report.Errors.ShouldBeEmpty();
        report.Unsettled.ShouldBeEmpty();
        var file = report.Files.ShouldHaveSingleItem();
        file.RelativePath.ShouldBe(@"ОВ\Клапаны\Клапан.rfa");
        file.Hash.Length.ShouldBe(64);
        file.CatalogHash.ShouldNotBeNull();
        File.Exists(Path.Combine(_root, ".toadflow", "history", file.Hash + ".rfa")).ShouldBeTrue();
        File.Exists(Path.Combine(_root, ".toadflow", "history", file.CatalogHash + ".txt")).ShouldBeTrue();
    }

    [Test]
    public async Task Scan_ReadsRevitYearOfRfa()
    {
        Put(@"КР\" + SampleName, Sample);

        var report = await ScanAsync();

        report.Files.ShouldHaveSingleItem().RevitYear.ShouldBe(2022);
    }

    [Test]
    public async Task Scan_SecondWalk_UsesCacheForUnchangedFiles()
    {
        var path = Put(@"ОВ\Клапан.rfa");
        var first = await ScanAsync();
        // Тот же размер и время — хеш берётся из кеша, даже если содержимое подменить
        var written = File.GetLastWriteTimeUtc(path);
        File.WriteAllText(path, "rfa ОВ\\Клапам.rfa"); // та же длина в байтах
        File.SetLastWriteTimeUtc(path, written);

        var second = await ScanAsync();

        second.Files.ShouldHaveSingleItem().Hash.ShouldBe(first.Files[0].Hash);
    }

    [Test]
    public async Task Scan_RootUnavailable_ReportsIncompleteWithError()
    {
        var report = await ScanAsync(Path.Combine(_root, "нет такой папки"));

        report.Complete.ShouldBeFalse();
        report.Files.ShouldBeEmpty();
        report.Errors.ShouldHaveSingleItem();
    }

    [Test]
    public async Task Scan_UnsettledPathsReported()
    {
        var path = Path.Combine(_root, "ОВ", "Новый.rfa");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "только что записан");

        var report = await ScanAsync();

        report.Files.ShouldBeEmpty();
        report.Unsettled.ShouldBe([@"ОВ\Новый.rfa"]);
        report.Complete.ShouldBeTrue();
    }

    [Test]
    public async Task Scan_KeepFailed_FileGoesToUnsettled()
    {
        Put(@"ОВ\Клапан.rfa");
        // Копию версии положить некуда: на месте папки history — файл
        Directory.CreateDirectory(Path.Combine(_root, ".toadflow"));
        File.WriteAllText(Path.Combine(_root, ".toadflow", "history"), "");

        var report = await ScanAsync();

        report.Files.ShouldBeEmpty();
        report.Unsettled.ShouldBe([@"ОВ\Клапан.rfa"]);
        report.Errors.ShouldHaveSingleItem().ShouldContain(@"ОВ\Клапан.rfa");
    }

    [Test]
    public async Task Scan_SecondStartWhileRunning_ReturnsFalse()
    {
        Put(@"ОВ\Клапан.rfa");
        var release = new TaskCompletionSource();
        var sent = 0;
        using var scan = new FamilyLibraryScan(async (_, _) =>
        {
            Interlocked.Increment(ref sent);
            await release.Task;
        }, _data, NullLogger<FamilyLibraryScan>.Instance);

        scan.Start(Request(_root)).ShouldBeTrue();
        scan.Start(Request(_root)).ShouldBeFalse();
        release.SetResult();
        await scan.Current!;
        scan.Start(Request(_root)).ShouldBeTrue();
        await scan.Current!;

        sent.ShouldBe(2);
    }

    [Test]
    public void Hello_AdvertisesFamilies()
    {
        AgentModules.For(addinOutdated: false).ShouldContain(ModuleKinds.FamiliesRead);
        AgentModules.For(addinOutdated: false).ShouldContain(ModuleKinds.FamiliesScan);
        // Старый аддин семейства не прочитает, а обход идёт без Revit
        AgentModules.For(addinOutdated: true).ShouldNotContain(ModuleKinds.FamiliesRead);
        AgentModules.For(addinOutdated: true).ShouldContain(ModuleKinds.FamiliesScan);
    }

    /// <summary>Первые <paramref name="failures" /> запросов — сбой сети или 503, потом сервер принимает.</summary>
    private sealed class FlakyServer(int failures) : HttpMessageHandler
    {
        public int Calls;
        public FamilyScanReportDto? Received;
        public string? Path;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var call = Interlocked.Increment(ref Calls);
            if (call <= failures)
            {
                if (call % 2 == 1) throw new HttpRequestException("сервер недоступен");
                return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
            }

            request.Content!.Headers.ContentEncoding.ShouldContain("gzip");
            request.Headers.GetValues("X-Agent-Key").ShouldHaveSingleItem().ShouldBe("ключ");
            await using var zip = new GZipStream(await request.Content.ReadAsStreamAsync(ct), CompressionMode.Decompress);
            Received = await JsonSerializer.DeserializeAsync<FamilyScanReportDto>(zip, JsonSerializerOptions.Web, ct);
            Path = request.RequestUri!.AbsolutePath;
            return new HttpResponseMessage(HttpStatusCode.OK);
        }
    }

    private static FamilyScanReporter Reporter(FlakyServer server, TimeSpan budget) =>
        new(new AgentConfig("http://toadflow:8080", Guid.NewGuid(), "ключ"), new AgentOptions { HttpMessageHandlerFactory = () => server },
            NullLogger<FamilyScanReporter>.Instance, NoDelays, budget);

    private static FamilyScanReportDto Report() =>
        new(Guid.NewGuid(), true, [new FamilyScanFileDto(@"ОВ\Клапан.rfa", 10, DateTime.UtcNow, new string('a', 64), null, 2022)], [], []);

    [Test]
    public async Task Report_RetriesWhenServerIsDown()
    {
        var server = new FlakyServer(failures: 3);
        var report = Report();

        var delivered = await Reporter(server, TimeSpan.FromMinutes(1)).SendAsync(report, CancellationToken.None);

        delivered.ShouldBeTrue();
        server.Calls.ShouldBe(4);
        server.Path.ShouldBe($"/api/agent/families/scans/{report.ScanId}");
        server.Received.ShouldNotBeNull().Files.ShouldHaveSingleItem().RelativePath.ShouldBe(@"ОВ\Клапан.rfa");
    }

    [Test]
    public async Task Report_DroppedAfterRetryBudget()
    {
        var server = new FlakyServer(failures: int.MaxValue);

        var delivered = await Reporter(server, TimeSpan.Zero).SendAsync(Report(), CancellationToken.None);

        delivered.ShouldBeFalse();
        server.Calls.ShouldBe(1);
    }
}
