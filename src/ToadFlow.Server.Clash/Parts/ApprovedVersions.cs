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

    /// <summary>
    ///     Состав части на момент утверждения — <see cref="VersionModel" /> в JSON. Прогоны версии берут модели и разделы отсюда, а не
    ///     из нынешней настройки части. null — версия утверждена до снимка состава: берётся нынешний состав.
    /// </summary>
    public string? ModelsJson { get; set; }

    public string? Comment { get; set; }
    public Guid UserId { get; set; }
    public DateTimeOffset ApprovedAt { get; set; }
}

/// <summary>Модель в утверждённой версии части: раздел — тот, что был при утверждении (поправка или разбор имени).</summary>
/// <param name="ModelName">Имя на момент утверждения — для «Что изменилось», если модель потом удалили.</param>
public sealed record VersionModel(Guid ModelId, string ModelName, string Discipline);

public static class VersionModels
{
    public static string Serialize(IEnumerable<VersionModel> models) =>
        JsonSerializer.Serialize(models.OrderBy(m => m.ModelId).ToList(), ToadFlow.Server.Data.ToadFlowDb.Json);

    /// <returns>null — снимка нет.</returns>
    public static IReadOnlyList<VersionModel>? Parse(string? json) =>
        json is null ? null : JsonSerializer.Deserialize<List<VersionModel>>(json, ToadFlow.Server.Data.ToadFlowDb.Json);

    /// <summary>Снимок нынешнего состава; модели без раздела в него не попадают (их не проверяют).</summary>
    public static IReadOnlyList<VersionModel> Of(IEnumerable<PartModelView> models) =>
        models.Where(m => m.Discipline is not null).Select(m => new VersionModel(m.ModelId, m.Name, m.Discipline!)).ToList();

    /// <summary>Один ли состав: те же модели с теми же разделами (раздел — без учёта регистра, как в правилах).</summary>
    public static bool Same(IReadOnlyList<VersionModel> a, IReadOnlyList<VersionModel> b) =>
        a.Count == b.Count && a.All(m => b.Any(o => o.ModelId == m.ModelId && string.Equals(o.Discipline, m.Discipline, StringComparison.OrdinalIgnoreCase)));
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
