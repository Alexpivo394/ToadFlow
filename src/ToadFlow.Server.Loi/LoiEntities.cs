using System.Text.Json;
using Alexpivo394.Revit.ParamCheck.Profiles;
using ToadFlow.Contracts.Loi;
using ToadFlow.Server.Data;

namespace ToadFlow.Server.Loi;

/// <summary>Профиль проверки LOI: правила ParamChecker, вид и ключевое слово РН по умолчанию. Модели задаёт расписание.</summary>
public sealed class LoiProfile
{
    public Guid Id { get; set; }

    /// <summary>Совпадает с именем в <see cref="ProfileJson" />; отдельной колонкой — для уникальности и сортировки.</summary>
    public string Name { get; set; } = "";

    /// <summary>
    ///     Группа профиля — обычно проект: «Полковая», «Донской». Имя уникально внутри группы, поэтому профили «ОВ» могут
    ///     быть в каждом проекте. Пусто — без группы. В файл ParamChecker не попадает.
    /// </summary>
    public string Group { get; set; } = "";

    /// <summary>Имя с группой — для отчётов, аналитики и списков, где профили разных групп стоят рядом.</summary>
    public string DisplayName => DisplayNameOf(Group, Name);

    public static string DisplayNameOf(string group, string name) => group.Length > 0 ? $"{group} · {name}" : name;

    /// <summary>Профиль в формате ParamChecker (<see cref="ProfileSerializer.SerializeProfile" />), без моделей.</summary>
    public string ProfileJson { get; set; } = "{}";

    public Guid UpdatedByUserId { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }

    public CheckProfile ToProfile() => ProfileSerializer.DeserializeProfile(ProfileJson);
}

/// <summary>Категории и имена параметров, снятые в Revit одного года, — для редактора правил.</summary>
public sealed class LoiCatalog
{
    public int RevitYear { get; set; }
    public LoiCatalogResult Catalog { get; set; } = new([], [], false);
    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>Настройки модуля в расписании (<c>Schedule.ModuleSettingsJson</c>).</summary>
/// <param name="ProfileId">Один профиль на всё расписание; null — по разделу модели, профили разделов — в <paramref name="Disciplines" />.</param>
/// <param name="ModelProfiles">Исключения: у этих моделей свой профиль — и при одном профиле, и по разделу.</param>
/// <param name="Disciplines">Профили разделов этого расписания (проекта): в разных проектах у раздела разные профили.</param>
/// <param name="FallbackProfileId">Профиль моделей, раздел которых не распознан или не назначен; null — такие модели пропускаются.</param>
/// <param name="ViewName">Вид, с которого собираются элементы; null — как в профиле, пусто — вся модель.</param>
/// <param name="WorksetKeyword">РН с этим словом не открываются; null — как в профиле.</param>
/// <param name="HistoryFile">База для дашбордов (.xlsx) с подстановками папки результатов; пусто — не ведётся.</param>
/// <param name="Analytics">Записывать итоги в аналитику ToadFlow (если она включена в настройках сервиса).</param>
public sealed record LoiScheduleSettings(
    Guid? ProfileId,
    string? ViewName = null,
    string? WorksetKeyword = null,
    bool SkipLinks = true,
    bool ProblemsOnly = false,
    string HistoryFile = "",
    bool Analytics = true,
    List<ModelProfile>? ModelProfiles = null,
    List<DisciplineProfile>? Disciplines = null,
    Guid? FallbackProfileId = null)
{
    public List<ModelProfile> ModelProfiles { get; init; } = ModelProfiles ?? [];

    public List<DisciplineProfile> Disciplines { get; init; } = Disciplines ?? [];

    /// <summary>Все профили, на которые ссылается расписание: один профиль, разделы, остальные модели, исключения.</summary>
    public IEnumerable<Guid> ProfileIds => ModelProfiles.Select(m => m.ProfileId)
        .Concat(ProfileId is { } id ? [id] : [])
        .Concat(ProfileId is null ? Disciplines.Select(d => d.ProfileId) : [])
        .Concat(ProfileId is null && FallbackProfileId is { } fallback ? [fallback] : []);

    public static LoiScheduleSettings? TryParse(JsonElement json)
    {
        try
        {
            var settings = json.Deserialize<LoiScheduleSettings>(ToadFlowDb.Json);
            return settings is null
                ? null
                : settings with
                {
                    ProfileId = settings.ProfileId == Guid.Empty ? null : settings.ProfileId,
                    HistoryFile = settings.HistoryFile ?? "",
                    ModelProfiles = settings.ModelProfiles.Where(m => !string.IsNullOrWhiteSpace(m.Model)).ToList(),
                    Disciplines = settings.Disciplines.Where(d => !string.IsNullOrWhiteSpace(d.Discipline))
                        .Select(d => d with { Discipline = d.Discipline.Trim() }).ToList(),
                    FallbackProfileId = settings.FallbackProfileId == Guid.Empty ? null : settings.FallbackProfileId
                };
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public static LoiScheduleSettings? TryParse(string json)
    {
        using var document = JsonDocument.Parse(string.IsNullOrWhiteSpace(json) ? "{}" : json);
        return TryParse(document.RootElement);
    }

    /// <summary>Настройки проверки модели: расписание переопределяет вид и РН профиля.</summary>
    public LoiCheckSettingsDto Resolve(CheckProfile profile, string historyFile) => new()
    {
        SkipLinks = SkipLinks,
        ViewName = (ViewName ?? profile.ViewName).Trim(),
        WorksetKeyword = (WorksetKeyword ?? profile.WorksetKeyword).Trim(),
        ProblemsOnly = ProblemsOnly,
        HistoryFile = historyFile
    };
}
