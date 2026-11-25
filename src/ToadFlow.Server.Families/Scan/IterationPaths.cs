namespace ToadFlow.Server.Families.Scan;

/// <summary>Файл в папке итераций: семейство и итерация по папкам; <see cref="Valid" /> — лежит по шаблону.</summary>
public sealed record IterationPath(string Family, string Iteration, bool Valid);

/// <summary>Пути папки итераций: <c>&lt;папка&gt;\&lt;семейство&gt;\&lt;итерация&gt;\&lt;семейство&gt;.rfa</c>.</summary>
public static class IterationPaths
{
    public const int MaxName = 100;

    /// <summary>null — файл не в папке итераций.</summary>
    public static IterationPath? Parse(string relativePath, string iterationsFolder)
    {
        var parts = relativePath.Replace('/', '\\').TrimStart('\\').Split('\\');
        if (parts.Length < 2 || !parts[0].Equals(iterationsFolder, StringComparison.OrdinalIgnoreCase)) return null;
        if (parts.Length != 4) return new IterationPath(parts.Length > 2 ? parts[1] : "", parts.Length > 3 ? parts[2] : "", false);
        var file = parts[3];
        var name = file.EndsWith(".rfa", StringComparison.OrdinalIgnoreCase) ? file[..^4] : file;
        return new IterationPath(parts[1], parts[2], name.Equals(parts[1], StringComparison.OrdinalIgnoreCase));
    }

    public static string PathOf(string iterationsFolder, string family, string iteration) => $@"{iterationsFolder}\{family}\{iteration}\{family}.rfa";

    /// <summary>Название итерации годится в имя папки Windows; null — годится, иначе — почему нет.</summary>
    public static string? NameError(string? name)
    {
        var trimmed = name?.Trim() ?? "";
        if (trimmed.Length == 0) return "Укажите название итерации";
        if (trimmed.Length > MaxName) return $"Название итерации — до {MaxName} знаков";
        if (trimmed.IndexOfAny(['\\', '/', ':', '*', '?', '"', '<', '>', '|']) >= 0 || trimmed.Any(char.IsControl) || trimmed.EndsWith('.') || trimmed.Trim('.').Length == 0)
            return "Название итерации — имя папки: без знаков \\ / : * ? \" < > | и без точки в конце";
        return null;
    }
}
