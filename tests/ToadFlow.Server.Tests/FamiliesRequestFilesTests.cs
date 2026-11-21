using System.Net;
using System.Net.Http.Headers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using ToadFlow.Contracts;
using ToadFlow.Server.Families.Requests;
using ToadFlow.Server.Tests.Infrastructure;
using static ToadFlow.Server.Tests.Infrastructure.FamiliesFixtures;

namespace ToadFlow.Server.Tests;

/// <summary>Вложения: загрузка до подачи, привязка к заявке и сообщению, просмотр в браузере только для картинок и PDF, удаление, лимит.</summary>
public class FamiliesRequestFilesTests : ServerTest
{
    public FamiliesRequestFilesTests() => Services = WithoutScanLoop;

    private sealed record Created(Guid Id, int Number);

    private sealed record Uploaded(Guid Id, string FileName, long Size, string ContentType);

    /// <summary>Сигнатура PNG и немного данных.</summary>
    private static readonly byte[] Png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 1, 2, 3, 4];

    private static readonly byte[] Pdf = "%PDF-1.7 каталог"u8.ToArray();

    private static async Task<Uploaded> UploadAsync(HttpClient client, byte[] bytes, string name, string type = "image/png")
    {
        using var form = new MultipartFormDataContent();
        var content = new ByteArrayContent(bytes);
        content.Headers.ContentType = new MediaTypeHeaderValue(type);
        form.Add(content, "file", name);
        var response = await client.PostAsync("/api/families/requests/uploads", form);
        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        return await response.ReadJsonAsync<Uploaded>();
    }

    private static async Task<HttpResponseMessage> CreateAsync(HttpClient client, params Guid[] files) =>
        await client.PostJsonAsync("/api/families/requests", new NewRequest("Клапан", null, "ОВ", null, null, FileIds: files));

    private static Task<RequestView> ViewAsync(HttpClient client, Guid id) => client.GetJsonAsync<RequestView>($"/api/families/requests/{id}");

    [Test]
    public async Task Upload_ThenAttachOnCreate()
    {
        var designer = await ClientAsync(UserRole.Designer);
        var screenshot = await UploadAsync(designer, Png, "скрин.png");
        var catalog = await UploadAsync(designer, Pdf, "каталог.pdf", "application/pdf");
        (screenshot.ContentType, catalog.ContentType).ShouldBe(("image/png", "application/pdf"));
        (await designer.GetAsync($"/api/families/requests/uploads/{screenshot.Id}")).StatusCode.ShouldBe(HttpStatusCode.OK);

        var created = await (await CreateAsync(designer, screenshot.Id, catalog.Id)).ReadJsonAsync<Created>();

        var view = await ViewAsync(designer, created.Id);
        view.Files.Select(f => (f.FileName, f.Inline, f.MessageId)).ShouldBe([("скрин.png", true, (Guid?)null), ("каталог.pdf", true, (Guid?)null)]);
        view.Request.ThumbnailFileId.ShouldBe(screenshot.Id);
        var image = await designer.GetAsync($"/api/families/requests/{created.Id}/files/{screenshot.Id}");
        (await image.Content.ReadAsByteArrayAsync()).ShouldBe(Png);
        (await designer.GetAsync($"/api/families/requests/uploads/{screenshot.Id}")).StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Test]
    public async Task Attach_ForeignOrUsedUpload_Refused()
    {
        var owner = await ClientAsync(UserRole.Designer);
        var thief = await ClientAsync(UserRole.Designer);
        var upload = await UploadAsync(owner, Png, "скрин.png");

        (await CreateAsync(thief, upload.Id)).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await thief.GetAsync($"/api/families/requests/uploads/{upload.Id}")).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await CreateAsync(owner, upload.Id)).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await CreateAsync(owner, upload.Id)).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Test]
    public async Task Inline_OnlyWhitelistedBySignature()
    {
        var designer = await ClientAsync(UserRole.Designer);
        var html = await UploadAsync(designer, "<html><script>alert(1)</script></html>"u8.ToArray(), "evil.png");
        var png = await UploadAsync(designer, Png, "скрин.png", "application/octet-stream");
        html.ContentType.ShouldBe("application/octet-stream");
        png.ContentType.ShouldBe("image/png");
        var created = await (await CreateAsync(designer, html.Id, png.Id)).ReadJsonAsync<Created>();

        var evil = await designer.GetAsync($"/api/families/requests/{created.Id}/files/{html.Id}?inline=1");
        evil.StatusCode.ShouldBe(HttpStatusCode.OK);
        evil.Content.Headers.ContentType!.MediaType.ShouldBe("application/octet-stream");
        evil.Content.Headers.ContentType!.MediaType.ShouldBe("application/octet-stream");
        evil.Headers.GetValues("X-Content-Type-Options").ShouldBe(["nosniff"]);

        var image = await designer.GetAsync($"/api/families/requests/{created.Id}/files/{png.Id}?inline=1");
        image.Content.Headers.ContentDisposition!.DispositionType.ShouldBe("inline");
        image.Content.Headers.ContentType!.MediaType.ShouldBe("image/png");
        (await designer.GetAsync($"/api/families/requests/{created.Id}/files/{png.Id}")).Content.Headers.ContentDisposition!.DispositionType.ShouldBe("attachment");
    }

    [Test]
    public async Task Message_WithFilesOnly()
    {
        var designer = await ClientAsync(UserRole.Designer);
        var created = await (await CreateAsync(designer)).ReadJsonAsync<Created>();
        var upload = await UploadAsync(designer, Png, "куда встанет.png");

        (await designer.PostJsonAsync($"/api/families/requests/{created.Id}/messages", new RequestMessageRequest(" "))).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await designer.PostJsonAsync($"/api/families/requests/{created.Id}/messages", new RequestMessageRequest(null, [upload.Id]))).EnsureSuccessStatusCode();

        var view = await ViewAsync(designer, created.Id);
        var message = view.Messages.Single(m => !m.IsEvent);
        message.FileIds.ShouldBe([upload.Id]);
        view.Files.Single().MessageId.ShouldBe(message.Id);
    }

    [Test]
    public async Task NeedInfo_AttachmentOnlyReplyReturnsToWork()
    {
        var designer = await ClientAsync(UserRole.Designer);
        var coordinator = await ClientAsync(UserRole.Operator);
        var created = await (await CreateAsync(designer)).ReadJsonAsync<Created>();
        (await coordinator.PostJsonAsync($"/api/families/requests/{created.Id}/ask", new RequestMessageRequest("Покажите, куда встанет"))).EnsureSuccessStatusCode();
        var upload = await UploadAsync(designer, Png, "вот.png");

        (await designer.PostJsonAsync($"/api/families/requests/{created.Id}/messages", new RequestMessageRequest(null, [upload.Id]))).EnsureSuccessStatusCode();

        (await ViewAsync(designer, created.Id)).Request.State.ShouldBe(RequestState.InWork);
    }

    [Test]
    public async Task Delete_UploaderOrStaff()
    {
        var designer = await ClientAsync(UserRole.Designer);
        var other = await ClientAsync(UserRole.Designer);
        var coordinator = await ClientAsync(UserRole.Operator);
        var first = await UploadAsync(designer, Png, "1.png");
        var second = await UploadAsync(designer, Png, "2.png");
        var created = await (await CreateAsync(designer, first.Id, second.Id)).ReadJsonAsync<Created>();
        (await other.PostAsync($"/api/families/requests/{created.Id}/join", null)).EnsureSuccessStatusCode();

        (await other.DeleteAsync($"/api/families/requests/{created.Id}/files/{first.Id}")).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await designer.DeleteAsync($"/api/families/requests/{created.Id}/files/{first.Id}")).EnsureSuccessStatusCode();
        (await coordinator.DeleteAsync($"/api/families/requests/{created.Id}/files/{second.Id}")).EnsureSuccessStatusCode();

        var view = await ViewAsync(designer, created.Id);
        view.Files.ShouldBeEmpty();
        view.Messages.Select(m => m.Text).ShouldContain("удалил файл «1.png»");
    }

    [Test]
    public async Task Limit_30()
    {
        var designer = await ClientAsync(UserRole.Designer);
        var uploads = new List<Guid>();
        for (var i = 0; i < 31; i++) uploads.Add((await UploadAsync(designer, Png, $"{i}.png")).Id);

        (await CreateAsync(designer, [.. uploads])).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await CreateAsync(designer, [.. uploads.Take(30)])).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Test]
    public async Task Maintenance_RemovesOrphanUploadsAfterADay()
    {
        var designer = await ClientAsync(UserRole.Designer);
        var upload = await UploadAsync(designer, Png, "забытый.png");

        await using (var scope = Server.Services.CreateAsyncScope())
            await RequestMaintenance.RunOnceAsync(scope.ServiceProvider, DateTimeOffset.UtcNow.AddDays(2), CancellationToken.None);

        (await DbAsync(db => db.Set<FamilyRequestFile>().AnyAsync(f => f.Id == upload.Id))).ShouldBeFalse();
    }
}
