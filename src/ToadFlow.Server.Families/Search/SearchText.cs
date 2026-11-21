using System.Text;
using System.Text.Json;
using Alexpivo394.Revit.Families.Files;
using ToadFlow.Server.Data;
using ToadFlow.Server.Families.Data;

namespace ToadFlow.Server.Families.Search;

/// <summary>
///     Текст, по которому ищется карточка: имя, путь (папки через пробел, без расширения), категория, производитель, описание,
///     метки, имена типоразмеров, имена параметров и значения параметров типоразмеров действующей версии — в нижнем регистре,
///     через пробел. Значения нужны, чтобы находилось обозначение вроде «КЛАБ-40», записанное в параметре, а не в имени типоразмера.
/// </summary>
public static class SearchText
{
    /// <summary>Значения типоразмеров обрезаются здесь: каталог на тысячи строк не должен раздувать строку поиска.</summary>
    public const int MaxLength = 64 * 1024;

    public static string Build(FamilyCard card, FamilyContent? content)
    {
        var parts = new List<string> { card.Name, PathWords(card.RelativePath), card.Category, card.Manufacturer ?? "", card.Description ?? "" };
        parts.AddRange(card.Tags);
        if (content is not null)
        {
            parts.AddRange(content.Types.Select(t => t.Name));
            parts.AddRange(content.Parameters.Select(p => p.Name));
        }

        var text = new StringBuilder(string.Join(" ", parts.Where(p => p.Length > 0)).ToLowerInvariant());
        if (content is not null)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var value in content.Types.SelectMany(t => t.Values.Values))
            {
                var word = value.Trim().ToLowerInvariant();
                if (word.Length == 0 || !seen.Add(word)) continue;
                if (text.Length + word.Length + 1 > MaxLength) break;
                text.Append(' ').Append(word);
            }
        }

        return text.ToString();
    }

    /// <summary>Содержимое версии, сохранённое фермой; null — ещё не прочитано или не читается.</summary>
    public static FamilyContent? ContentOf(FamilyVersion? version)
    {
        if (version?.ContentJson is not { } json) return null;
        try
        {
            return JsonSerializer.Deserialize<FamilyContent>(json, ToadFlowDb.Json);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string PathWords(string relativePath)
    {
        var withoutExtension = relativePath.EndsWith(".rfa", StringComparison.OrdinalIgnoreCase) ? relativePath[..^4] : relativePath;
        return withoutExtension.Replace('\\', ' ').Replace('/', ' ');
    }
}
