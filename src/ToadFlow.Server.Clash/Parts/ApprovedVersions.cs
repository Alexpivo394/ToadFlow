using System.Text.Json;
using ToadFlow.Server.Events;

namespace ToadFlow.Server.Clash.Parts;

/// <summary>
///     Утверждённая версия конфигурации части. Номер растёт с каждым утверждением, хранятся все версии: по ним видно, какой
///     версией найдена коллизия. Расписания прогоняют последнюю (<c>ClashPart.ApprovedVersion</c>).
/// </summary>
public sealed class ClashPartVersion
{
    public Guid PartId { get; set; }
    public int Version { get; set; }
    public string ConfigurationJson { get; set; } = "";
    public string? Comment { get; set; }
    public Guid UserId { get; set; }
    public DateTimeOffset ApprovedAt { get; set; }
}

/// <summary>Утвердить черновик; <paramref name="Run" /> — сразу прогнать утверждённую версию на последних выгрузках.</summary>
/// <param name="Schedules">Расписания регулярного прогона части; null — выбор не меняется. Применяется в той же транзакции.</param>
public sealed record ApproveRequest(string? Comment, bool Run, IReadOnlyList<Guid>? Schedules = null);

/// <param name="RunId">Прогон утверждённой версии, поставленный при утверждении; null — не ставился.</param>
public sealed record ApprovedVersionView(int Version, DateTimeOffset ApprovedAt, string UserName, string? Comment, Guid? RunId = null);

public sealed record ApprovedVersionDetails(int Version, DateTimeOffset ApprovedAt, string UserName, string? Comment, JsonElement Configuration);

/// <summary>Черновик части утверждён новой версией — в «События системы».</summary>
public sealed record ClashPartApproved(Actor Actor, Guid PartId, string PartName, int Version) : DomainEvent(Actor)
{
    public override string Describe() => $"Утверждена версия {Version} части {PartName}";
    public override string UiEntity => "clash-parts";
    public override string UiId => PartId.ToString();
}
