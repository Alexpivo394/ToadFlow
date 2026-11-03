using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Time.Testing;
using Shouldly;
using ToadFlow.Contracts;
using ToadFlow.Server.Tests.Infrastructure;

namespace ToadFlow.Server.Tests;

/// <summary>Задание в фазе загрузки слепка Revit уже не держит: аренду продлевает heartbeat по списку загружаемых попыток.</summary>
public class HeartbeatUploadTests : ServerTest
{
    private readonly FakeTimeProvider _time = new(DateTimeOffset.UtcNow);

    public HeartbeatUploadTests() => Time = _time;

    [Test]
    public async Task Heartbeat_UploadingJob_LeaseExtended()
    {
        await using var agent = await FakeAgent.RegisterAsync(Server, await AdminAsync());
        await agent.ConnectAsync(new InstalledRevitDto(2022, true));
        await DbAsync(async db =>
        {
            Seed.Job(db, Seed.Run(db).Id, maxAttempts: 2);
            await db.SaveChangesAsync();
        });
        var job = (await agent.RequestJobAsync(1, 2022)).ShouldNotBeNull();
        // Аренда почти истекла; время не сдвигается — фиктивные часы рвут соединение SignalR по таймауту клиента.
        var issued = _time.GetUtcNow().AddSeconds(10);
        await DbAsync(db => db.Jobs.Where(j => j.Id == job.JobId).ExecuteUpdateAsync(j => j.SetProperty(x => x.LeaseUntil, issued)));

        // Чужая (устаревшая) попытка аренду не продлевает.
        await agent.HeartbeatAsync([], [new ActiveJobDto(job.JobId, job.Attempt + 1)]);
        (await LeaseAsync(job.JobId)).ShouldNotBeNull().ShouldBe(issued, TimeSpan.FromMilliseconds(1));

        await agent.HeartbeatAsync([], [new ActiveJobDto(job.JobId, job.Attempt)]);
        (await LeaseAsync(job.JobId)).ShouldNotBeNull().ShouldBe(_time.GetUtcNow() + TimeSpan.FromMinutes(2), TimeSpan.FromMilliseconds(1));
    }

    private Task<DateTimeOffset?> LeaseAsync(Guid jobId) => DbAsync(db => db.Jobs.AsNoTracking().Where(j => j.Id == jobId).Select(j => j.LeaseUntil).SingleAsync());
}
