using System.Net;
using Shouldly;
using ToadFlow.Contracts;
using ToadFlow.Server.Tests.Infrastructure;

namespace ToadFlow.Server.Tests;

public class SourceTests : ServerTest
{
    private sealed record SourceView(Guid Id, string Name, SourceKind Kind, string Host, int? RevitYear, string RootPath);

    [Test]
    public async Task Source_CreateRevitServer_ThenList()
    {
        var admin = await AdminAsync();

        var response = await admin.PostJsonAsync("/api/sources",
            new { name = "RS 2022", kind = SourceKind.RevitServer, host = "srv-rs22", revitYear = 2022, rootPath = "" });

        response.StatusCode.ShouldBe(HttpStatusCode.Created);
        var list = await (await ClientAsync(UserRole.Viewer)).GetJsonAsync<SourceView[]>("/api/sources");
        list.ShouldHaveSingleItem().Host.ShouldBe("srv-rs22");
    }

    [Test]
    public async Task Source_Folder_HasNoYear()
    {
        var admin = await AdminAsync();

        var created = await (await admin.PostJsonAsync("/api/sources",
            new { name = "NAS", kind = SourceKind.Folder, host = @"\\nas\projects", revitYear = 2024, rootPath = "" })).ReadJsonAsync<SourceView>();

        created.RevitYear.ShouldBeNull();
    }

    [Test]
    public async Task Source_RevitServerWithoutYear_400()
    {
        var admin = await AdminAsync();

        var response = await admin.PostJsonAsync("/api/sources",
            new { name = "RS", kind = SourceKind.RevitServer, host = "srv", revitYear = (int?)null, rootPath = "" });

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Test]
    [Arguments(2019)]
    [Arguments(2028)]
    public async Task Source_YearOutOfRange_400(int year)
    {
        var admin = await AdminAsync();

        var response = await admin.PostJsonAsync("/api/sources",
            new { name = "RS", kind = SourceKind.RevitServer, host = "srv", revitYear = year, rootPath = "" });

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync()).ShouldContain("2020");
    }

    [Test]
    public async Task Source_FolderNotAbsolute_400()
    {
        var admin = await AdminAsync();

        var response = await admin.PostJsonAsync("/api/sources",
            new { name = "NAS", kind = SourceKind.Folder, host = "projects", revitYear = (int?)null, rootPath = "" });

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Test]
    public async Task Source_UsedBySchedule_DeleteConflict()
    {
        var admin = await AdminAsync();
        var sourceId = await DbAsync(async db =>
        {
            var source = Seed.Source(db);
            Seed.Schedule(db, source.Id);
            await db.SaveChangesAsync();
            return source.Id;
        });

        (await admin.DeleteAsync($"/api/sources/{sourceId}")).StatusCode.ShouldBe(HttpStatusCode.Conflict);
    }

    [Test]
    public async Task Source_OperatorCannotCreate_403()
    {
        var operatorClient = await ClientAsync(UserRole.Operator);

        (await operatorClient.PostJsonAsync("/api/sources",
                new { name = "RS", kind = SourceKind.RevitServer, host = "srv", revitYear = 2022, rootPath = "" }))
            .StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }
}
