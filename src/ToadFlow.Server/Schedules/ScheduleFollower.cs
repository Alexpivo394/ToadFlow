namespace ToadFlow.Server.Schedules;

/// <summary>Человек следит за расписанием: ему приходят уведомления об ошибках его запусков. Создатель следит с создания.</summary>
public sealed class ScheduleFollower
{
    public Guid ScheduleId { get; set; }

    public Guid UserId { get; set; }
}
