using System.Text.Json;

namespace ToadFlow.Server.Loi;

/// <summary>
///     Имена <c>BuiltInCategory</c> по числу. В файлах ParamChecker прежних версий категории записаны числами, а у сервера
///     нет Revit API, чтобы их перевести. Числа категорий от версии Revit не меняются; таблица снята с RevitAPI 2020–2024 —
///     старые файлы новее не бывают.
/// </summary>
internal static class BuiltInCategories
{
    private static readonly Lazy<IReadOnlyDictionary<int, string>> Names = new(Load);

    public static string? Name(int id) => Names.Value.GetValueOrDefault(id);

    private static IReadOnlyDictionary<int, string> Load()
    {
        using var stream = typeof(BuiltInCategories).Assembly.GetManifestResourceStream("BuiltInCategories.json")
                           ?? throw new InvalidOperationException("Нет ресурса BuiltInCategories.json");
        return JsonSerializer.Deserialize<Dictionary<int, string>>(stream)!;
    }
}
