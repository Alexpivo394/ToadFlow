using Alexpivo394.Revit.ParamCheck.Profiles;
using Microsoft.EntityFrameworkCore;
using ToadFlow.Server.Data;
using ToadFlow.Server.Loi.Analytics;
using ToadFlow.Server.Loi.Naming;

namespace ToadFlow.Server.Loi;

/// <summary>
///     Какой профиль у модели какого раздела — для расписаний «по разделу модели». Общая для всех расписаний: так одно
///     расписание охватывает весь проект, а новый проект не нужно настраивать заново. Раздел модели — из справочника
///     именования аналитики (с ручными поправками).
/// </summary>
/// <param name="FallbackProfileId">Профиль моделей, раздел которых не распознан или не сопоставлен; null — такие модели пропускаются.</param>
public sealed record LoiProfileRouting(List<DisciplineProfile> Disciplines, Guid? FallbackProfileId = null)
{
    public static LoiProfileRouting Empty => new([]);

    public IEnumerable<Guid> ProfileIds => Disciplines.Select(d => d.ProfileId).Concat(FallbackProfileId is { } id ? [id] : []);
}

public sealed record DisciplineProfile(string Discipline, Guid ProfileId);

/// <summary>Исключение расписания: этой модели — этот профиль, что бы ни говорил раздел.</summary>
/// <param name="Model">Имя модели без папок и расширения; регистр не важен.</param>
public sealed record ModelProfile(string Model, Guid ProfileId);

public enum ProfileVia
{
    /// <summary>Исключение расписания для этой модели.</summary>
    Model,

    /// <summary>Один профиль на всё расписание.</summary>
    Schedule,

    Discipline,
    Fallback,

    /// <summary>Профиля нет — модель пропускается.</summary>
    None
}

public sealed record ProfileChoice(Guid? ProfileId, string? Discipline, ProfileVia Via);

/// <summary>Выбирает профиль модели расписания. Живёт в области одного запуска: маршруты и справочник читаются один раз.</summary>
public sealed class LoiProfileResolver(LoiSettingsStore settings, CheckRecorder recorder)
{
    private LoiProfileRouting? _routing;
    private ModelNameParser? _parser;

    public async Task<ProfileChoice> ChooseAsync(LoiScheduleSettings schedule, string modelName, string modelPath, CancellationToken ct)
    {
        var name = ModelNames.FromPath(modelName);
        if (schedule.ModelProfiles.FirstOrDefault(m => string.Equals(ModelNames.FromPath(m.Model), name, StringComparison.OrdinalIgnoreCase)) is { } exception)
            return new ProfileChoice(exception.ProfileId, null, ProfileVia.Model);
        if (schedule.ProfileId is { } fixedProfile) return new ProfileChoice(fixedProfile, null, ProfileVia.Schedule);

        _routing ??= await settings.RoutingAsync(ct);
        _parser ??= new ModelNameParser(await settings.NamingAsync(ct));
        var discipline = (await recorder.PlaceAsync(_parser, CheckRecorder.ModelKey(modelPath, modelName), modelPath, modelName, ct)).Discipline;

        if (discipline is not null &&
            _routing.Disciplines.FirstOrDefault(d => string.Equals(d.Discipline.Trim(), discipline, StringComparison.OrdinalIgnoreCase)) is { } routed)
            return new ProfileChoice(routed.ProfileId, discipline, ProfileVia.Discipline);

        return _routing.FallbackProfileId is { } fallback
            ? new ProfileChoice(fallback, discipline, ProfileVia.Fallback)
            : new ProfileChoice(null, discipline, ProfileVia.None);
    }

    /// <summary>Почему модель пропущена — для предупреждения запуска.</summary>
    public static string SkipReason(ProfileChoice choice) => choice.Discipline is { } discipline
        ? $"разделу «{discipline}» не назначен профиль LOI"
        : "раздел не распознан по имени, а профиля для остальных моделей нет";

    /// <summary>Профили, на которые ссылаются расписания и маршруты, — их нельзя удалить.</summary>
    public static async Task<bool> IsUsedAsync(ToadFlowDb db, LoiSettingsStore settings, Guid profileId)
    {
        if ((await settings.RoutingAsync()).ProfileIds.Contains(profileId)) return true;
        return (await db.Schedules.Where(s => s.ModuleKind == Contracts.ModuleKinds.Loi).Select(s => s.ModuleSettingsJson).ToListAsync())
            .Select(LoiScheduleSettings.TryParse)
            .Any(s => s is not null && s.ProfileIds.Contains(profileId));
    }
}
