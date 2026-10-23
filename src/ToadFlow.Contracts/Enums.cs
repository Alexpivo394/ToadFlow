namespace ToadFlow.Contracts;

public enum UserRole
{
    Viewer,
    Operator,
    Admin
}

public enum AgentState
{
    Online,
    Offline,
    Draining,
    Disabled
}

public enum InstanceState
{
    Starting,
    Idle,
    Busy,
    Unresponsive,
    Restarting
}

public enum JobStatus
{
    Queued,
    Running,
    Succeeded,
    Warning,
    Failed,
    Cancelled
}

/// <summary>Чем закончилось задание по мнению агента. <see cref="InfrastructureFailure" /> — Revit завис, упал или убит: повторяется.</summary>
public enum JobOutcome
{
    Succeeded,
    Warning,
    Failed,
    InfrastructureFailure,
    Cancelled
}

public enum RunState
{
    Expanding,
    Queued,
    Running,
    Completed,
    Cancelled
}

public enum SourceKind
{
    RevitServer,
    Folder
}

public enum RecurrenceKind
{
    Weekly,
    EveryNHours,
    Once
}

public enum StepStatus
{
    NotRequested,
    Done,
    Skipped,
    Failed
}
