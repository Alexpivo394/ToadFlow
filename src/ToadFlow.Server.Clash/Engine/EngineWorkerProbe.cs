using ToadFlow.Server.Overview;

namespace ToadFlow.Server.Clash.Engine;

/// <summary>Движок коллизий как рабочий процесс модуля: «Обзор» и страница «Сервер» показывают, на связи ли он.</summary>
internal sealed class EngineWorkerProbe(EngineLiveness liveness) : IWorkerProbe
{
    public string Module => "clash";

    public string Title => "Движок коллизий";

    public DateTimeOffset? LastSeenAt => liveness.LastSeenAt;

    public DateTimeOffset StartedAt => liveness.StartedAt;
}
