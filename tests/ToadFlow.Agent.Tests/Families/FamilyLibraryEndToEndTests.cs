using System.Net;
using System.Net.Http.Json;
using Alexpivo394.Revit.Families.Files;
using Alexpivo394.Revit.Families.Files.Scan;
using Microsoft.EntityFrameworkCore;
using Shouldly;
using ToadFlow.Agent.Tests.Infrastructure;
using ToadFlow.Agent.Tests.Integration;
using ToadFlow.Contracts;
using ToadFlow.Server.Families.Actions;
using ToadFlow.Server.Families.Cards;
using ToadFlow.Server.Families.Data;
using ToadFlow.Server.Families.Panel;
using ToadFlow.Server.Families.Requests;
using ToadFlow.Server.Tests.Infrastructure;

namespace ToadFlow.Agent.Tests.Families;

/// <summary>
///     Сквозь всё, кроме Revit: настоящий агент обходит настоящую папку и делает с файлами то, что просят кнопки веба; сервер ведёт карточки,
///     публикации и заявки. Плагин Revit изображается тем, что он делает: кладёт файл в библиотеку и сообщает о публикации.
/// </summary>
[NotInParallel(nameof(AgentPaths))]
public class FamilyLibraryEndToEndTests : ServerTest
{
    private static readonly string Sample = Path.Combine(AppContext.BaseDirectory, "Families", "samples", "Образец 2022.rfa");

    private readonly FakeLauncher _launcher = new();
    private readonly Config.AgentConfigStoreTests.TempRoot _data = new();
    private readonly string _root = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "toadflow-e2e-lib-" + Guid.NewGuid().ToString("N"))).FullName;
    private AgentHarness? _agent;
    private HttpClient _admin = null!;

    public FamilyLibraryEndToEndTests() => Services = FamiliesFixtures.WithoutScanLoop;

    [After(Test)]
    public async Task CleanupAsync()
    {
        if (_agent is not null) await _agent.DisposeAsync();
        _data.Dispose();
        try
        {
            Directory.Delete(_root, true);
        }
        catch (IOException)
        {
            // временная папка
        }
    }

    /// <summary>Семейство в библиотеке: образец 2022 с добавкой, чтобы содержимое различалось; записано давно — обход берёт сразу.</summary>
    private string Put(string relative, string variant)
    {
        var path = Path.Combine(_root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, [.. File.ReadAllBytes(Sample), .. System.Text.Encoding.UTF8.GetBytes(variant)]);
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(-30));
        return FamilyHash.OfFile(path);
    }

    private async Task StartAsync()
    {
        _admin = await AdminAsync();
        _agent = await AgentHarness.StartAsync(Server, _admin, _launcher, machineName: "BIM-LIB-E2E");
        await EventuallyAsync(() => Task.FromResult(_agent.Connection.IsConnected));
        (await _admin.PutJsonAsync("/api/families/settings", new { root = _root, reviewFolder = "_На проверке", scanIntervalMinutes = 10, libraryAgentId = (Guid?)null }))
            .EnsureSuccessStatusCode();
    }

    /// <summary>Ждёт, пока все начатые обходы получат отчёт.</summary>
    private async Task SettledAsync()
    {
        try
        {
            await EventuallyAsync(async () => !await DbAsync(db => db.Set<FamilyScan>().AnyAsync(s => s.FinishedAt == null)), 30_000);
        }
        catch (TimeoutException)
        {
            var scans = await DbAsync(db => db.Set<FamilyScan>().AsNoTracking().OrderBy(s => s.StartedAt).ToListAsync());
            throw new TimeoutException("Обход без отчёта: " + string.Join("; ", scans.Select(s => $"{s.StartedAt:HH:mm:ss.fff} fin={s.FinishedAt:HH:mm:ss.fff} files={s.Files} err={string.Join("|", s.Errors)}")));
        }
    }

    /// <summary>«Обойти сейчас», пока условие не выполнится: файл, который только что записали, обход берёт со второго взгляда.</summary>
    private async Task ScanUntilAsync(Func<Task<bool>> condition)
    {
        for (var attempt = 0; attempt < 4; attempt++)
        {
            await SettledAsync();
            if (await condition()) return;
            var started = await _admin.PostAsync("/api/families/scan", null);
            started.StatusCode.ShouldBeOneOf(HttpStatusCode.OK, HttpStatusCode.Conflict); // 409 — обход уже идёт
        }

        await SettledAsync();
        if (await condition()) return;

        throw new TimeoutException("Обходы не дали ожидаемого");
    }

    private Task<FamilyCard> CardAsync(string name) => DbAsync(db => db.Set<FamilyCard>().AsNoTracking().SingleAsync(c => c.Name == name));

    private Task<FamilyCardView> ViewAsync(Guid id) => _admin.GetJsonAsync<FamilyCardView>($"/api/families/{id}");

    private async Task<string?> CurrentHashAsync(string name)
    {
        var card = await CardAsync(name);
        return (await ViewAsync(card.Id)).Versions.SingleOrDefault(v => v.IsCurrent)?.Hash;
    }

    [Test]
    public async Task PublishApproveRevertArchiveRestore_OnRealFiles()
    {
        await StartAsync();
        var first = Put(@"ОВ\Клапан.rfa", "v1");
        await ScanUntilAsync(async () => await DbAsync(db => db.Set<FamilyCard>().AnyAsync(c => c.Name == "Клапан")));
        var card = await CardAsync("Клапан");
        (card.State, card.RevitYear).ShouldBe((CardState.Approved, 2022));
        File.Exists(HistoryStore.PathOf(_root, first, ".rfa")).ShouldBeTrue(); // копия версии — сразу при обходе

        // Плагин: новая версия существующего — в папку проверки под тем же именем, с описанием
        var second = Put(@"_На проверке\Клапан.rfa", "v2");
        (await _admin.PostJsonAsync("/api/families/publications", new PublishRequest(Path.Combine(_root, @"_На проверке\Клапан.rfa"), second, card.Id, "добавлен DN200", null)))
            .EnsureSuccessStatusCode();
        await ScanUntilAsync(async () => (await ViewAsync(card.Id)).Versions.Any(v => v.Hash == second));
        var candidate = (await ViewAsync(card.Id)).Versions.Single(v => v.Hash == second);
        (candidate.State, candidate.Comment, candidate.AuthorName).ShouldBe((VersionState.InReview, "добавлен DN200", "Администратор"));

        // Веб: утвердить кандидата — агент кладёт его на место прежнего файла
        (await _admin.PostAsync($"/api/families/versions/{candidate.Id}/approve", null)).EnsureSuccessStatusCode();
        await ScanUntilAsync(async () => await CurrentHashAsync("Клапан") == second);
        File.Exists(Path.Combine(_root, @"_На проверке\Клапан.rfa")).ShouldBeFalse();
        FamilyHash.OfFile(Path.Combine(_root, @"ОВ\Клапан.rfa")).ShouldBe(second);

        // Веб: вернуть первую версию — агент берёт её из истории
        var firstVersion = (await ViewAsync(card.Id)).Versions.Single(v => v.Hash == first && !v.IsCurrent);
        (await _admin.PostAsync($"/api/families/versions/{firstVersion.Id}/revert", null)).EnsureSuccessStatusCode();
        await ScanUntilAsync(async () => await CurrentHashAsync("Клапан") == first);
        (await ViewAsync(card.Id)).Versions.Single(v => v.IsCurrent).Note.ShouldStartWith("возврат к версии от ");
        // Revit в тестовом агенте — заглушка без содержимого: «не читается» здесь ожидаемо; правок мимо проверки после возврата быть не должно
        (await DbAsync(db => db.Set<FamilyIssue>().Where(i => i.ResolvedAt == null && i.Kind != IssueKind.Unreadable).Select(i => i.Kind + ": " + i.Text).ToListAsync())).ShouldBeEmpty();

        // Веб: в архив и обратно — карточка та же, история цела
        (await _admin.PostAsync($"/api/families/{card.Id}/archive", null)).EnsureSuccessStatusCode();
        await ScanUntilAsync(async () => (await CardAsync("Клапан")).State == CardState.Archived);
        File.Exists(Path.Combine(_root, @"ОВ\Клапан.rfa")).ShouldBeFalse();
        (await _admin.PostAsync($"/api/families/{card.Id}/restore", null)).EnsureSuccessStatusCode();
        await ScanUntilAsync(async () => (await CardAsync("Клапан")).State == CardState.Approved);
        var restored = await CardAsync("Клапан");
        restored.Id.ShouldBe(card.Id);
        FamilyHash.OfFile(Path.Combine(_root, @"ОВ\Клапан.rfa")).ShouldBe(first);
    }

    [Test]
    public async Task RequestClosedByPublishedFamily_ThenApprovedIntoFolder()
    {
        await StartAsync();
        var designer = await ClientAsync(UserRole.Designer);
        var created = await (await designer.PostJsonAsync("/api/families/requests", new NewRequest("Задвижка клиновая DN100", "", "ОВ", RequestUrgency.Normal, "")))
            .Content.ReadFromJsonAsync<CreatedRequest>();

        var hash = Put(@"_На проверке\Задвижка клиновая.rfa", "z");
        (await _admin.PostJsonAsync("/api/families/publications",
            new PublishRequest(Path.Combine(_root, @"_На проверке\Задвижка клиновая.rfa"), hash, null, "по заявке", created!.Id))).EnsureSuccessStatusCode();
        await ScanUntilAsync(async () => await DbAsync(db => db.Set<FamilyCard>().AnyAsync(c => c.Name == "Задвижка клиновая")));

        var request = await designer.GetJsonAsync<RequestView>($"/api/families/requests/{created.Id}");
        (request.Request.State, request.Request.CardName, request.Request.Unread).ShouldBe((RequestState.Done, "Задвижка клиновая", true));

        var card = await CardAsync("Задвижка клиновая");
        card.State.ShouldBe(CardState.InReview);
        (await _admin.PostJsonAsync($"/api/families/{card.Id}/approve", new ApproveRequest(@"ОВ\Арматура"))).EnsureSuccessStatusCode();
        await ScanUntilAsync(async () => (await CardAsync("Задвижка клиновая")).State == CardState.Approved);
        (await CardAsync("Задвижка клиновая")).RelativePath.ShouldBe(@"ОВ\Арматура\Задвижка клиновая.rfa");
        File.Exists(Path.Combine(_root, @"ОВ\Арматура\Задвижка клиновая.rfa")).ShouldBeTrue();
        (await designer.GetJsonAsync<ToadFlow.Server.Runs.Page<ToadFlow.Server.Families.Search.FamilyHit>>("/api/families/search?q=задвижка")).Total.ShouldBe(1);
    }

    private sealed record CreatedRequest(Guid Id, int Number);
}
