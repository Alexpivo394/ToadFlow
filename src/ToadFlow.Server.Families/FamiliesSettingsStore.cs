using System.Text.Json;
using System.Text.RegularExpressions;
using Alexpivo394.Revit.Families.Files.Scan;
using Microsoft.EntityFrameworkCore;
using ToadFlow.Server.Data;
using ToadFlow.Server.Data.Entities;

namespace ToadFlow.Server.Families;

/// <summary>Параметры модуля «Семейства».</summary>
/// <param name="Root">Корень библиотеки — путь UNC или с буквой диска, как его видит агент библиотеки; пусто — библиотека не задана.</param>
/// <param name="ReviewFolder">Имя папки проверки в корне.</param>
/// <param name="ScanIntervalMinutes">Как часто обходить библиотеку.</param>
/// <param name="AutoAcceptDays">Через сколько дней на приёмке заявка принимается сама, если автор молчит.</param>
/// <param name="LibraryAgentId">Агент, который обходит библиотеку; null — любой в сети, умеющий обход.</param>
public sealed partial record FamiliesSettings(string Root = "", string ReviewFolder = FamiliesSettings.DefaultReviewFolder, int ScanIntervalMinutes = 10,
    Guid? LibraryAgentId = null, int AutoAcceptDays = 5)
{
    public const string DefaultReviewFolder = "_На проверке";

    [System.Text.Json.Serialization.JsonIgnore]
    public bool Configured => Root.Length > 0;

    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();
        if (Root.Length > 0 && !UncPath().IsMatch(Root) && !DrivePath().IsMatch(Root))
            errors.Add(@"Корень библиотеки — путь UNC (\\сервер\папка) или с буквой диска (Y:\Семейства)");
        if (ReviewFolder.Length == 0 || ReviewFolder.IndexOfAny(['\\', '/', ':', '*', '?', '"', '<', '>', '|']) >= 0 || ReviewFolder.Trim('.').Length == 0
            || ReviewFolder.Equals(LibraryPaths.ServiceFolder, StringComparison.OrdinalIgnoreCase))
            errors.Add(@"Папка проверки — одно имя папки в корне библиотеки, без \ и /");
        if (ScanIntervalMinutes is < 1 or > 1440) errors.Add("Интервал обхода — от 1 до 1440 минут");
        if (AutoAcceptDays is < 1 or > 60) errors.Add("Автоприёмка — от 1 до 60 дней");
        return errors;
    }

    /// <summary>Без пробелов по краям и без разделителя в конце; корень диска (<c>Y:\</c>) остаётся с разделителем.</summary>
    public FamiliesSettings Normalized()
    {
        var root = Root.Trim();
        var trimmed = root.TrimEnd('\\', '/');
        root = trimmed.Length == 2 && trimmed[1] == ':' ? trimmed + @"\" : trimmed;
        return this with { Root = root, ReviewFolder = ReviewFolder.Trim() };
    }

    [GeneratedRegex(@"^\\\\[^\\/]+\\[^\\/]+")]
    private static partial Regex UncPath();

    [GeneratedRegex(@"^[A-Za-z]:\\")]
    private static partial Regex DrivePath();
}

/// <summary>Параметры модуля в общей таблице настроек.</summary>
public sealed class FamiliesSettingsStore(ToadFlowDb db)
{
    private const string Key = "families.settings";

    public async Task<FamiliesSettings> GetAsync(CancellationToken ct = default)
    {
        var row = await db.Settings.AsNoTracking().SingleOrDefaultAsync(s => s.Key == Key, ct);
        return row is null ? new FamiliesSettings() : JsonSerializer.Deserialize<FamiliesSettings>(row.Value, ToadFlowDb.Json) ?? new FamiliesSettings();
    }

    public async Task SaveAsync(FamiliesSettings settings, CancellationToken ct = default)
    {
        var row = await db.Settings.SingleOrDefaultAsync(s => s.Key == Key, ct);
        if (row is null) db.Settings.Add(row = new Setting { Key = Key });
        row.Value = JsonSerializer.Serialize(settings, ToadFlowDb.Json);
        await db.SaveChangesAsync(ct);
    }
}
