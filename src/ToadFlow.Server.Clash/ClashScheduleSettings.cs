using System.Text.Json;
using ToadFlow.Server.Data;

namespace ToadFlow.Server.Clash;

/// <summary>Настройки выгрузки в расписании (<c>Schedule.ModuleSettingsJson</c>).</summary>
/// <param name="ViewName">3D-вид — фильтр: выгружаются только видимые на нём элементы.</param>
/// <param name="WorksetKeyword">Рабочие наборы с этим словом в имени не открываются; пусто — открываются все.</param>
/// <param name="Parts">Утверждённые части, которые прогоняются, когда закончатся все задания запуска; пусто — только выгрузка.</param>
public sealed record ClashScheduleSettings(string ViewName = ClashScheduleSettings.DefaultView, string WorksetKeyword = "", IReadOnlyList<Guid>? Parts = null)
{
    public const string DefaultView = "Navisworks";

    public static ClashScheduleSettings? TryParse(JsonElement json)
    {
        try
        {
            var settings = json.Deserialize<ClashScheduleSettings>(ToadFlowDb.Json);
            return settings is null
                ? null
                : settings with
                {
                    ViewName = (settings.ViewName ?? "").Trim(), WorksetKeyword = (settings.WorksetKeyword ?? "").Trim(), Parts = settings.Parts ?? []
                };
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
