namespace ToadFlow.Server.Data.Entities;

public enum ActorKind
{
    User,
    Agent,
    System
}

/// <summary>Строка вкладки «События системы»: кто и что сделал.</summary>
public sealed class SystemEvent
{
    public long Id { get; set; }
    public DateTimeOffset At { get; set; }
    public ActorKind ActorKind { get; set; }
    public string ActorName { get; set; } = "";
    public string Kind { get; set; } = "";
    public string Text { get; set; } = "";
}

public sealed class Setting
{
    public string Key { get; set; } = "";
    public string Value { get; set; } = "";
}
