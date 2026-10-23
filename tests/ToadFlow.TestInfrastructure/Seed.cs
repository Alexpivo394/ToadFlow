using ToadFlow.Contracts;
using ToadFlow.Server.Data;
using ToadFlow.Server.Data.Entities;

namespace ToadFlow.Server.Tests.Infrastructure;

/// <summary>Быстрое заполнение базы для тестов в обход API. Сущности добавляются в контекст; сохраняет вызывающий.</summary>
public static class Seed
{
    public static User User(ToadFlowDb db, UserRole role = UserRole.Operator, string? login = null)
    {
        var user = Data.Entities.User.Create(login ?? "u" + Guid.NewGuid().ToString("N")[..8], "Тестовый пользователь", role, "x",
            DateTimeOffset.UtcNow);
        db.Users.Add(user);
        return user;
    }

    public static Source Source(ToadFlowDb db, SourceKind kind = SourceKind.RevitServer, int? year = 2022)
    {
        var source = new Source
        {
            Id = Guid.CreateVersion7(),
            Name = "Источник " + Guid.NewGuid().ToString("N")[..4],
            Kind = kind,
            Host = kind == SourceKind.RevitServer ? "rs22" : @"\\nas\projects",
            RevitYear = kind == SourceKind.RevitServer ? year : null,
            RootPath = ""
        };
        db.Sources.Add(source);
        return source;
    }

    public static Run Run(ToadFlowDb db, RunState state = RunState.Queued, Guid? scheduleId = null)
    {
        var run = new Run { Id = Guid.CreateVersion7(), Name = "ЖК Север", ScheduleId = scheduleId, State = state, CreatedAt = DateTimeOffset.UtcNow };
        db.Runs.Add(run);
        return run;
    }

    public static Job Job(ToadFlowDb db, Guid runId, int year = 2022, bool requiresNwc = false, int priority = 0, int maxAttempts = 1,
        DateTimeOffset? createdAt = null, string name = "АР.rvt")
    {
        var job = new Job
        {
            Id = Guid.CreateVersion7(),
            RunId = runId,
            ModuleKind = ModuleKinds.Export,
            ModelName = name,
            ModelPath = "RSN://rs22/" + name,
            RevitYear = year,
            RequiresNwc = requiresNwc,
            PayloadJson = """{"modelName":"АР.rvt"}""",
            Status = JobStatus.Queued,
            MaxAttempts = maxAttempts,
            Priority = priority,
            TimeoutMinutes = 30,
            CreatedAt = createdAt ?? DateTimeOffset.UtcNow
        };
        db.Jobs.Add(job);
        return job;
    }

    public static Schedule Schedule(ToadFlowDb db, Guid sourceId, Guid? userId = null)
    {
        var author = userId ?? User(db).Id;
        var now = DateTimeOffset.UtcNow;
        var schedule = new Schedule
        {
            Id = Guid.CreateVersion7(),
            Name = "ЖК Север",
            ModuleKind = ModuleKinds.Export,
            SourceId = sourceId,
            OutputFolderTemplate = @"\\nas\exports\{дата}",
            Selection = new SelectionDto([], [new FolderSelectionDto("", true, true)]),
            ModuleSettingsJson = "{}",
            RecurrenceKind = RecurrenceKind.Weekly,
            DaysOfWeekMask = 0b0011111,
            TimeOfDay = new TimeOnly(20, 0),
            ModelTimeoutMinutes = 30,
            MaxRetries = 1,
            IsEnabled = true,
            CreatedByUserId = author,
            CreatedAt = now,
            UpdatedByUserId = author,
            UpdatedAt = now
        };
        db.Schedules.Add(schedule);
        return schedule;
    }
}
