using System.Text;
using System.Text.RegularExpressions;
using Alexpivo394.Revit.ParamCheck.Profiles;

namespace ToadFlow.Server.Loi.Naming;

/// <summary>Что удалось понять из имени модели.</summary>
/// <param name="Fields">Свои поля из справочника (например «Стадия»).</param>
/// <param name="Unrecognized">Куски имени, которых нет ни в справочнике, ни в шаблонах, — их стоит добавить в настройках.</param>
public sealed record ModelClassification(
    string? Customer,
    string? Object,
    string? Discipline,
    string? Part,
    IReadOnlyDictionary<string, string> Fields,
    IReadOnlyList<string> Unrecognized);

/// <summary>
///     Разбирает имя модели по <see cref="NamingConfig" />. Куски сравниваются без учёта регистра, пробелов и раскладки:
///     русская «К» в <c>K1-К3</c> — то же, что латинская, а русское <c>АР</c> — то же, что <c>AR</c>. Значение поля задаёт
///     первый подошедший кусок.
/// </summary>
public sealed class ModelNameParser
{
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromMilliseconds(200);

    /// <summary>Кириллические буквы, неотличимые на вид от латинских, — их путают при наборе имён.</summary>
    private static readonly Dictionary<char, char> Lookalikes = new()
    {
        ['А'] = 'A', ['В'] = 'B', ['Е'] = 'E', ['К'] = 'K', ['М'] = 'M', ['Н'] = 'H', ['О'] = 'O', ['Р'] = 'P', ['С'] = 'C', ['Т'] = 'T',
        ['У'] = 'Y', ['Х'] = 'X'
    };

    /// <summary>Транслитерация: коды, набранные по-русски (<c>АР</c>, <c>ОВ</c>, <c>КЖ</c>), совпадают с латинскими (<c>AR</c>, <c>OV</c>, <c>KZH</c>).</summary>
    private static readonly Dictionary<char, string> Translit = new()
    {
        ['А'] = "A", ['Б'] = "B", ['В'] = "V", ['Г'] = "G", ['Д'] = "D", ['Е'] = "E", ['Ё'] = "E", ['Ж'] = "ZH", ['З'] = "Z", ['И'] = "I",
        ['Й'] = "Y", ['К'] = "K", ['Л'] = "L", ['М'] = "M", ['Н'] = "N", ['О'] = "O", ['П'] = "P", ['Р'] = "R", ['С'] = "S", ['Т'] = "T",
        ['У'] = "U", ['Ф'] = "F", ['Х'] = "KH", ['Ц'] = "TS", ['Ч'] = "CH", ['Ш'] = "SH", ['Щ'] = "SCH", ['Ъ'] = "", ['Ы'] = "Y", ['Ь'] = "",
        ['Э'] = "E", ['Ю'] = "YU", ['Я'] = "YA"
    };

    private readonly char[] _separators;
    private readonly List<(string[][] Parts, NamingEntry Entry)> _combined;
    private readonly Dictionary<string, NamingEntry> _single = new();
    private readonly Dictionary<(string Field, string Key), NamingEntry> _byField = new();
    private readonly List<(Regex Regex, NamingPattern Pattern)> _patterns = [];
    private readonly List<Regex> _expressions = [];

    public ModelNameParser(NamingConfig config)
    {
        _separators = config.Separators.Length > 0 ? config.Separators.ToCharArray() : ['_'];

        var entries = config.Entries.Where(e => !string.IsNullOrWhiteSpace(e.Tokens)).Select(e => (Parts: Parts(e.Tokens), Entry: e)).ToList();
        _combined = entries.Where(e => e.Parts.Length > 1).OrderByDescending(e => e.Parts.Length).ToList();

        // Сначала основные написания всех записей, потом запасные: запись «P» важнее запасного написания русской «Р».
        var single = entries.Where(e => e.Parts.Length == 1).ToList();
        for (var form = 0; form < 2; form++)
        {
            foreach (var (parts, entry) in single.Where(e => e.Parts[0].Length > form))
            {
                _single.TryAdd(parts[0][form], entry);
                _byField.TryAdd((entry.Field, parts[0][form]), entry);
            }
        }

        foreach (var pattern in config.Patterns)
            if (TryCompile(pattern.Pattern, RegexOptions.None) is { } regex) _patterns.Add((regex, pattern));
        foreach (var expression in config.NameExpressions)
            if (TryCompile(expression, RegexOptions.IgnoreCase) is { } regex) _expressions.Add(regex);
    }

    /// <summary>Ошибки настроек для показа пользователю; пусто — всё в порядке.</summary>
    public static IReadOnlyList<string> Validate(NamingConfig config)
    {
        var errors = new List<string>();
        if (config.Separators.Length == 0) errors.Add("Укажите хотя бы один разделитель");
        foreach (var entry in config.Entries)
        {
            if (string.IsNullOrWhiteSpace(entry.Field)) errors.Add($"У записи «{entry.Tokens}» не указано поле");
            if (Parts(entry.Tokens).Length == 0) errors.Add($"У записи «{entry.Value}» не указаны куски имени");
            if (entry.Field != NamingFields.Ignore && string.IsNullOrWhiteSpace(entry.Value)) errors.Add($"У записи «{entry.Tokens}» не указано значение");
        }

        foreach (var pattern in config.Patterns.Select(p => p.Pattern).Concat(config.NameExpressions))
            if (TryCompile(pattern, RegexOptions.None) is null) errors.Add($"Неверное регулярное выражение «{pattern}»");
        return errors;
    }

    /// <param name="modelNameOrPath">Имя модели или путь к ней — папки и расширение отбрасываются.</param>
    public ModelClassification Parse(string modelNameOrPath)
    {
        var name = ModelNames.FromPath(modelNameOrPath);
        return ParseExpressions(name) ?? ParseTokens(name);
    }

    /// <summary>Кусок имени для сравнения: без пробелов, заглавными, латиницей вместо похожих русских букв.</summary>
    public static string Normalize(string token) => Convert(token, c => Lookalikes.TryGetValue(c, out var latin) ? latin.ToString() : null);

    /// <summary>
    ///     Написания куска для сравнения: латиница вместо похожих русских букв и транслитерация. Кусок, набранный
    ///     целиком по-русски, скорее транслитерируется (<c>АР</c> — это <c>AR</c>), а в куске с латиницей русские буквы —
    ///     скорее опечатка (<c>К1-K3</c>), поэтому основным считается соответствующее написание.
    /// </summary>
    public static string[] Forms(string token)
    {
        var lookalike = Normalize(token);
        var translit = Convert(token, c => Translit.GetValueOrDefault(c));
        string[] forms = token.Any(char.IsAsciiLetter) ? [lookalike, translit] : [translit, lookalike];
        return forms.Where(f => f.Length > 0).Distinct().ToArray();
    }

    private static string Convert(string token, Func<char, string?> replace)
    {
        var builder = new StringBuilder(token.Length);
        foreach (var c in token.ToUpperInvariant())
        {
            if (char.IsWhiteSpace(c)) continue;
            builder.Append(replace(c) ?? c.ToString());
        }

        return builder.ToString();
    }

    private ModelClassification? ParseExpressions(string name)
    {
        foreach (var expression in _expressions)
        {
            Match match;
            try
            {
                match = expression.Match(name);
            }
            catch (RegexMatchTimeoutException)
            {
                continue;
            }

            if (!match.Success) continue;

            NamingEntry? Find(string field, string text) =>
                Forms(text).Select(form => _byField.GetValueOrDefault((field, form))).FirstOrDefault(entry => entry is not null);

            string? Group(string field)
            {
                var group = match.Groups[field];
                if (!group.Success || group.Value.Trim().Length == 0) return null;
                return Find(field, group.Value)?.Value ?? group.Value.Trim();
            }

            var objectEntry = match.Groups[NamingFields.Object] is { Success: true } o ? Find(NamingFields.Object, o.Value) : null;
            return new ModelClassification(Group("customer") ?? objectEntry?.Customer, Group(NamingFields.Object), Group(NamingFields.Discipline),
                Group(NamingFields.Part), new Dictionary<string, string>(), []);
        }

        return null;
    }

    private ModelClassification ParseTokens(string name)
    {
        var tokens = name.Split(_separators).Select(t => t.Trim()).Where(t => t.Length > 0).ToList();
        var forms = tokens.Select(Forms).ToList();
        var used = new bool[tokens.Count];
        var values = new Dictionary<string, string>();
        string? customer = null;

        void Assign(NamingEntry entry, string value)
        {
            if (entry.Field == NamingFields.Ignore || values.ContainsKey(entry.Field)) return;
            values[entry.Field] = value;
            if (entry.Field == NamingFields.Object) customer ??= string.IsNullOrWhiteSpace(entry.Customer) ? null : entry.Customer;
        }

        // Записи из нескольких кусков первыми: «DON+BNK-DOO» важнее, чем просто «DON».
        foreach (var (parts, entry) in _combined)
        {
            var positions = new List<int>();
            foreach (var part in parts)
            {
                var position = Enumerable.Range(0, tokens.Count).FirstOrDefault(i => !used[i] && !positions.Contains(i) && forms[i].Intersect(part).Any(), -1);
                if (position < 0) break;
                positions.Add(position);
            }

            if (positions.Count < parts.Length) continue;
            foreach (var position in positions) used[position] = true;
            Assign(entry, entry.Value);
        }

        var unrecognized = new List<string>();
        for (var i = 0; i < tokens.Count; i++)
        {
            if (used[i]) continue;
            if (forms[i].Select(form => _single.GetValueOrDefault(form)).FirstOrDefault(entry => entry is not null) is { } entry)
            {
                Assign(entry, entry.Value);
                continue;
            }

            if (MatchPattern(forms[i]) is { } found) Assign(new NamingEntry(found.Pattern.Field, ""), found.Value);
            else unrecognized.Add(tokens[i]);
        }

        var custom = values.Where(pair => !NamingFields.IsBuiltIn(pair.Key)).ToDictionary(pair => pair.Key, pair => pair.Value);
        return new ModelClassification(customer, values.GetValueOrDefault(NamingFields.Object), values.GetValueOrDefault(NamingFields.Discipline),
            values.GetValueOrDefault(NamingFields.Part), custom, unrecognized);
    }

    private (NamingPattern Pattern, string Value)? MatchPattern(string[] forms)
    {
        foreach (var (regex, pattern) in _patterns)
        {
            foreach (var form in forms)
            {
                try
                {
                    var match = regex.Match(form);
                    if (match.Success) return (pattern, pattern.Value.Length == 0 ? "" : match.Result(pattern.Value));
                }
                catch (RegexMatchTimeoutException)
                {
                    // шаблон слишком тяжёлый для этого куска — считаем, что не подошёл
                }
            }
        }

        return null;
    }

    /// <summary>Куски записи (через «+»), у каждого — его написания.</summary>
    private static string[][] Parts(string tokens) => tokens.Split('+').Select(Forms).Where(f => f.Length > 0).ToArray();

    private static Regex? TryCompile(string pattern, RegexOptions options)
    {
        try
        {
            return new Regex(pattern, options | RegexOptions.CultureInvariant, RegexTimeout);
        }
        catch (ArgumentException)
        {
            return null;
        }
    }
}
