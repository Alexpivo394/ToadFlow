using System.IO.Compression;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using ToadFlow.Contracts;
using ToadFlow.Contracts.Families;
using Alexpivo394.Revit.Families.Files;
using ToadFlow.Server.Data;
using ToadFlow.Server.Families.Cards;
using ToadFlow.Server.Families;
using ToadFlow.Server.Families.Data;
using ToadFlow.Server.Families.Scan;

namespace ToadFlow.Server.Tests.Infrastructure;

/// <summary>Карточки, версии и отчёты обхода библиотеки семейств для тестов.</summary>
public static class FamiliesFixtures
{
    public const string Root = @"\\nas\Families";

    /// <summary>Виды и возможности агента новой версии.</summary>
    public static readonly string[] AgentModules =
        [ModuleKinds.Export, ModuleKinds.Loi, ModuleKinds.LoiCatalog, ModuleKinds.ClashExtract, ModuleKinds.FamiliesRead, ModuleKinds.FamiliesScan, ModuleKinds.FamiliesFiles];

    /// <summary>Без фонового обхода по интервалу и обслуживания заявок: тест сам решает, когда обходить и обслуживать.</summary>
    public static void WithoutScanLoop(IServiceCollection services)
    {
        foreach (var hosted in services.Where(s => s.ImplementationType == typeof(LibraryScanLoop) || s.ImplementationType == typeof(ToadFlow.Server.Families.Read.FamilyReadLoop)
                                                     || s.ImplementationType?.Name == "RequestMaintenance").ToList())
            services.Remove(hosted);
    }

    public static FamilyCard Card(string name, string relativePath, CardState state, DateTimeOffset at)
    {
        var card = new FamilyCard { Id = Guid.CreateVersion7(), RelativePath = relativePath, State = state, CreatedAt = at, UpdatedAt = at };
        card.SetName(name);
        return card;
    }

    /// <summary>Карточка с действующей версией; с <paramref name="content" /> — уже прочитанной фермой.</summary>
    public static async Task<Guid> SeedCardAsync(ToadFlowDb db, string name, string relativePath, CardState state, FamilyContent? content, DateTimeOffset at,
        string? description = null, int? year = 2022)
    {
        var card = Card(name, relativePath, state, at);
        card.Section = Alexpivo394.Revit.Families.Files.Scan.LibraryPaths.Section(relativePath, FamiliesSettings.DefaultReviewFolder);
        card.Description = description;
        if (state == CardState.Archived) card.ArchivedAt = at;
        var version = new FamilyVersion
        {
            Id = Guid.CreateVersion7(),
            CardId = card.Id,
            Hash = Hash(relativePath),
            RevitYear = year,
            State = state == CardState.InReview ? VersionState.InReview : VersionState.Approved,
            RelativePath = relativePath,
            ReadState = content is null ? ReadState.Pending : ReadState.Done,
            ContentJson = content is null ? null : JsonSerializer.Serialize(content, ToadFlowDb.Json),
            SeenAt = at
        };
        card.CurrentVersionId = version.Id;
        CardFields.Refresh(card, version);
        db.Set<FamilyCard>().Add(card);
        db.Set<FamilyVersion>().Add(version);
        await db.SaveChangesAsync();
        return card.Id;
    }

    public static string Hash(string content) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(content))).ToLowerInvariant();

    /// <summary>Файл в отчёте обхода: хеш — от <paramref name="content" />, хеш каталога — от <paramref name="catalog" />.</summary>
    public static FamilyScanFileDto File(string relativePath, string content, string? catalog = null, int? year = 2022) =>
        new(relativePath, content.Length, new DateTime(2026, 11, 1, 0, 0, 0, DateTimeKind.Utc), Hash(content), catalog is null ? null : Hash(catalog), year);
}

/// <summary>Библиотека семейств с агентом библиотеки на связи: тест присылает отчёты обхода от его имени.</summary>
public sealed class FamilyLibrary : IAsyncDisposable
{
    private readonly ServerFactory _server;

    private FamilyLibrary(ServerFactory server, HttpClient admin, FakeAgent agent)
    {
        _server = server;
        Admin = admin;
        Agent = agent;
    }

    public HttpClient Admin { get; }

    public FakeAgent Agent { get; }

    /// <param name="modules">Что сообщает агент; по умолчанию — агент новой версии с обходом и чтением.</param>
    public static async Task<FamilyLibrary> StartAsync(ServerFactory server, HttpClient admin, string reviewFolder = FamiliesSettings.DefaultReviewFolder,
        IReadOnlyList<string>? modules = null, params InstalledRevitDto[] revits)
    {
        var agent = await FakeAgent.RegisterAsync(server, admin, "BIM-LIB-01");
        await agent.ConnectAsync("2.5.0", modules ?? FamiliesFixtures.AgentModules,
            revits.Length > 0 ? revits : [new InstalledRevitDto(2022, true), new InstalledRevitDto(2024, true)]);
        var saved = await admin.PutJsonAsync("/api/families/settings", new FamiliesSettings(FamiliesFixtures.Root, reviewFolder, 10, null));
        saved.EnsureSuccessStatusCode();
        return new FamilyLibrary(server, admin, agent);
    }

    /// <summary>Просит обход и присылает его отчёт; возвращает номер обхода.</summary>
    public async Task<Guid> ScanAsync(params FamilyScanFileDto[] files) => await ScanAsync(files, complete: true);

    public async Task<Guid> ScanAsync(IReadOnlyList<FamilyScanFileDto> files, bool complete, IReadOnlyList<string>? unsettled = null,
        IReadOnlyList<string>? errors = null)
    {
        var scanId = await RequestScanAsync();
        (await ReportAsync(new FamilyScanReportDto(scanId, complete, files, errors ?? (complete ? [] : ["ОВ: нет доступа"]), unsettled ?? [])))
            .EnsureSuccessStatusCode();
        return scanId;
    }

    /// <summary>Отчёт последнего обхода, о котором попросили агента (публикация или действие с файлом начинают обход сами).</summary>
    public async Task ReportLastAsync(params FamilyScanFileDto[] files)
    {
        var scanId = Agent.ScanRequests.Last().ScanId;
        (await ReportAsync(new FamilyScanReportDto(scanId, true, files, [], []))).EnsureSuccessStatusCode();
    }

    /// <summary>«Обойти сейчас» от имени администратора: номер обхода, который получил агент.</summary>
    public async Task<Guid> RequestScanAsync()
    {
        var response = await Admin.PostAsync("/api/families/scan", null);
        if (!response.IsSuccessStatusCode) throw new InvalidOperationException(await response.Content.ReadAsStringAsync());
        var scanId = (await response.ReadJsonAsync<ScanStarted>()).ScanId;
        Agent.ScanRequests.ShouldContainScan(scanId);
        return scanId;
    }

    /// <summary>Отчёт обхода от имени агента библиотеки — как его отправляет агент: сжатый gzip.</summary>
    public async Task<HttpResponseMessage> ReportAsync(FamilyScanReportDto report, Guid? scanId = null)
    {
        using var packed = new MemoryStream();
        await using (var zip = new GZipStream(packed, CompressionLevel.Fastest, leaveOpen: true))
            await JsonSerializer.SerializeAsync(zip, report, TestJson.Options);
        var content = new ByteArrayContent(packed.ToArray());
        content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        content.Headers.ContentEncoding.Add("gzip");

        using var http = _server.CreateClient();
        http.DefaultRequestHeaders.Add("X-Agent-Id", Agent.Id.ToString());
        http.DefaultRequestHeaders.Add("X-Agent-Key", Agent.Key);
        return await http.PostAsync($"/api/agent/families/scans/{scanId ?? report.ScanId}", content);
    }

    public ValueTask DisposeAsync() => Agent.DisposeAsync();

    private sealed record ScanStarted(Guid ScanId);
}

internal static class ScanRequestAssertions
{
    public static void ShouldContainScan(this System.Collections.Concurrent.ConcurrentQueue<FamilyScanRequestDto> requests, Guid scanId)
    {
        if (!requests.Any(r => r.ScanId == scanId)) throw new InvalidOperationException($"Агент не получил просьбу обойти библиотеку {scanId}");
    }
}
