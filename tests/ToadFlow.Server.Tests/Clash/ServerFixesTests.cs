using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using ToadFlow.Contracts;
using ToadFlow.Contracts.Clash;
using ToadFlow.Server.Clash;
using ToadFlow.Server.Clash.Engine;
using ToadFlow.Server.Clash.Uploads;
using ToadFlow.Server.Data.Entities;
using ToadFlow.Server.Tests.Infrastructure;
using static ToadFlow.Server.Tests.Infrastructure.ClashDraftFixtures;

namespace ToadFlow.Server.Tests;

/// <summary>Схема коллизий, публикация слепка, расписание без папки, уборка заявок.</summary>
public class ServerFixesTests : ServerTest
{
    [Test]
    public async Task PartDelete_RemovesDraftHits()
    {
        var admin = await AdminAsync();
        await using var agent = await FakeAgent.RegisterAsync(Server, admin);
        await agent.ConnectAsync("1.8.0", new InstalledRevitDto(2022, true));
        var partId = await PartAsync(Server, admin, agent, "POL_K1_AR.rvt", "POL_K1_OV.rvt");
        await DbAsync(async db =>
        {
            db.Set<ClashDraftHit>().Add(new ClashDraftHit { RunId = Guid.NewGuid(), Number = 1, PartId = partId, ModelA = "a", UniqueIdA = "ua", ModelB = "b", UniqueIdB = "ub" });
            await db.SaveChangesAsync();
        });

        (await admin.DeleteAsync($"/api/clash/parts/{partId}")).StatusCode.ShouldBe(HttpStatusCode.NoContent);

        (await DbAsync(db => db.Set<ClashDraftHit>().CountAsync(h => h.PartId == partId))).ShouldBe(0);
    }

    [Test]
    public async Task CollisionModelIndexes_Exist()
    {
        var indexes = await DbAsync(db => db.Database
            .SqlQuery<string>($"SELECT indexname AS \"Value\" FROM pg_indexes WHERE tablename = 'clash_collisions'").ToListAsync());

        indexes.ShouldContain("ix_clash_collisions_model_a");
        indexes.ShouldContain("ix_clash_collisions_model_b");
    }

    /// <summary>
    ///     Запись слепка не удалась уже после переноса папки в хранилище: папка возвращается на место, слепка без записи не остаётся, а
    ///     повтор «complete» агентом публикует загрузку.
    /// </summary>
    [Test]
    public async Task Publish_CancelAfterMove_RecordCommittedOrFolderRestored()
    {
        var admin = await AdminAsync();
        await using var agent = await FakeAgent.RegisterAsync(Server, admin);
        await agent.ConnectAsync("1.8.0", new InstalledRevitDto(2022, true));
        var (jobId, model) = await ClashFixtures.RunningJobAsync(Server, agent);
        var files = ClashFixtures.ModelFiles(model.SnapshotKey);
        var client = ClashFixtures.AgentClient(Server, agent);
        var upload = await (await client.PostJsonAsync("/api/agent/clash/uploads", files.Request(jobId))).ReadJsonAsync<ClashUploadState>();
        foreach (var (file, bytes) in new[] { (ClashUploads.Geometry, files.Geometry), (ClashUploads.Parameters, files.Parameters) })
            (await client.PutAsync($"/api/agent/clash/uploads/{upload.UploadId}/{file}?offset=0", new ByteArrayContent(bytes))).EnsureSuccessStatusCode();
        await DbAsync(db => db.Database.ExecuteSqlRawAsync("""
            CREATE FUNCTION fail_snapshot() RETURNS trigger AS $$ BEGIN RAISE EXCEPTION 'запись не удалась'; END $$ LANGUAGE plpgsql;
            CREATE TRIGGER fail_snapshot BEFORE INSERT ON clash_snapshots FOR EACH ROW EXECUTE FUNCTION fail_snapshot();
            """));

        try
        {
            var failed = await client.PostAsync($"/api/agent/clash/uploads/{upload.UploadId}/complete", null);
            failed.IsSuccessStatusCode.ShouldBeFalse();
        }
        catch (Exception exception) when (exception is not ShouldAssertException)
        {
            // Тестовый сервер отдаёт необработанное исключение клиенту.
        }

        var storage = Server.Services.GetRequiredService<ClashStorage>();
        Directory.Exists(storage.UploadFolder(upload.UploadId)).ShouldBeTrue();
        (Directory.Exists(storage.ModelFolder(model.Id)) ? Directory.GetDirectories(storage.ModelFolder(model.Id)) : []).ShouldBeEmpty();

        await DbAsync(db => db.Database.ExecuteSqlRawAsync("DROP TRIGGER fail_snapshot ON clash_snapshots"));
        var retried = await client.PostAsync($"/api/agent/clash/uploads/{upload.UploadId}/complete", null);
        retried.StatusCode.ShouldBe(HttpStatusCode.OK, await retried.Content.ReadAsStringAsync());
        var snapshot = (await retried.ReadJsonAsync<ClashExtractResult>()).SnapshotId;
        Directory.Exists(storage.SnapshotFolder(model.Id, snapshot)).ShouldBeTrue();
    }

    [Test]
    public async Task Schedule_NullOutputFolder_Saved()
    {
        var admin = await AdminAsync();
        var sourceId = await ScheduleApi.SourceAsync(admin);

        var response = await admin.PostJsonAsync("/api/schedules", new
        {
            name = "Коллизии без папки",
            moduleKind = ModuleKinds.ClashExtract,
            sourceId,
            outputFolderTemplate = (string?)null,
            selection = new SelectionDto([new ModelSelectionDto("АР.rvt", "RSN://rs22/ЖК/АР.rvt", true, false)], []),
            moduleSettings = new { viewName = "Navisworks" },
            recurrenceKind = RecurrenceKind.Weekly,
            daysOfWeekMask = 0b0011111,
            timeOfDay = "20:00:00",
            intervalHours = (int?)null,
            onceAt = (DateTimeOffset?)null,
            priority = 0,
            modelTimeoutMinutes = 60,
            maxRetries = 1,
            isEnabled = true
        });

        response.StatusCode.ShouldBe(HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
    }

    /// <summary>Заявка истекла: вход и списки заявок идут одновременно — ни одного 500, заявка удалена ровно один раз.</summary>
    [Test]
    public async Task ExpiredRequests_ConcurrentCleanup_No500()
    {
        var admin = await AdminAsync();
        await DbAsync(async db =>
        {
            var user = User.Create("expired-1", "Просроченный", UserRole.Designer, "", DateTimeOffset.UtcNow.AddDays(-4));
            user.PasswordHash = new Microsoft.AspNetCore.Identity.PasswordHasher<User>().HashPassword(user, UserPassword);
            user.IsPending = true;
            db.Users.Add(user);
            await db.SaveChangesAsync();
        });

        var requests = Enumerable.Range(0, 12).Select(i => i % 2 == 0
            ? admin.GetAsync("/api/users/pending")
            : Server.CreateClient().PostJsonAsync("/api/auth/login", new { login = "expired-1", password = UserPassword }));
        var responses = await Task.WhenAll(requests);

        responses.ShouldAllBe(r => (int)r.StatusCode < 500);
        responses.Where(r => r.RequestMessage!.RequestUri!.AbsolutePath == "/api/auth/login").ShouldAllBe(r => r.StatusCode == HttpStatusCode.Unauthorized);
        (await DbAsync(db => db.Users.AnyAsync(u => u.Login == "expired-1"))).ShouldBeFalse();
        await EventuallyAsync(async () => await DbAsync(db => db.SystemEvents.AnyAsync(e => e.Text.Contains("expired-1"))));
        (await DbAsync(db => db.SystemEvents.CountAsync(e => e.Text.Contains("expired-1")))).ShouldBe(1);
    }
}
