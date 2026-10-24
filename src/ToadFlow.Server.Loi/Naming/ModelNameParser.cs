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
///     русская «К» в <c>K1-К3</c> — то же, что латинская. Значение поля задаёт первый подошедший кусок.
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

    private readonly char[] _separators;
    private readonly List<(string[] Keys, NamingEntry Entry)> _combined;
    private readonly Dictionary<string, NamingEntry> _single = new();
    private readonly Dictionary<(string Field, string Key), NamingEntry> _byField = new();
    private readonly List<(Regex Regex, NamingPattern Pattern)> _patterns = [];
    private readonly List<Regex> _expressions = [];

    public ModelNameParser(NamingConfig config)
    {
        _separators = config.Separators.Length > 0 ? config.Separators.ToCharArray() : ['_'];

        var entries = config.Entries.Where(e => !string.IsNullOrWhiteSpace(e.Tokens)).Select(e => (Keys: Keys(e.Tokens), Entry: e)).ToList();
        _combined = entries.Where(e => e.Keys.Length > 1).OrderByDescending(e => e.Keys.Length).ToList();
        foreach (var (keys, entry) in entries.Where(e => e.Keys.Length == 1))
        {
            _single.TryAdd(keys[0], entry);
            _byField.TryAdd((entry.Field, keys[0]), entry);
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
            if (Keys(entry.Tokens).Length == 0) errors.Add($"У записи «{entry.Value}» не указаны куски имени");
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
    public static string Normalize(string token)
    {
        var builder = new StringBuilder(token.Length);
        foreach (var c in token.ToUpperInvariant())
        {
            if (char.IsWhiteSpace(c)) continue;
            builder.Append(Lookalikes.GetValueOrDefault(c, c));
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

            string? Group(string field)
            {
                var group = match.Groups[field];
                if (!group.Success || group.Value.Trim().Length == 0) return null;
                return _byField.TryGetValue((field, Normalize(group.Value)), out var entry) ? entry.Value : group.Value.Trim();
            }

            var objectEntry = match.Groups[NamingFields.Object] is { Success: true } o ? _byField.GetValueOrDefault((NamingFields.Object, Normalize(o.Value))) : null;
            return new ModelClassification(Group("customer") ?? objectEntry?.Customer, Group(NamingFields.Object), Group(NamingFields.Discipline),
                Group(NamingFields.Part), new Dictionary<string, string>(), []);
        }

        return null;
    }

    private ModelClassification ParseTokens(string name)
    {
        var tokens = name.Split(_separators).Select(t => t.Trim()).Where(t => t.Length > 0).ToList();
        var keys = tokens.Select(Normalize).ToList();
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
            var positions = parts.Select(part => Enumerable.Range(0, keys.Count).FirstOrDefault(i => !used[i] && keys[i] == part, -1)).ToList();
            if (positions.Any(p => p < 0)) continue;
            foreach (var position in positions) used[position] = true;
            Assign(entry, entry.Value);
        }

        var unrecognized = new List<string>();
        for (var i = 0; i < tokens.Count; i++)
        {
            if (used[i]) continue;
            if (_single.TryGetValue(keys[i], out var entry))
            {
                Assign(entry, entry.Value);
                continue;
            }

            if (MatchPattern(keys[i]) is { } found) Assign(new NamingEntry(found.Pattern.Field, ""), found.Value);
            else unrecognized.Add(tokens[i]);
        }

        var custom = values.Where(pair => !NamingFields.IsBuiltIn(pair.Key)).ToDictionary(pair => pair.Key, pair => pair.Value);
        return new ModelClassification(customer, values.GetValueOrDefault(NamingFields.Object), values.GetValueOrDefault(NamingFields.Discipline),
            values.GetValueOrDefault(NamingFields.Part), custom, unrecognized);
    }

    private (NamingPattern Pattern, string Value)? MatchPattern(string key)
    {
        foreach (var (regex, pattern) in _patterns)
        {
            try
            {
                var match = regex.Match(key);
                if (match.Success) return (pattern, pattern.Value.Length == 0 ? "" : match.Result(pattern.Value));
            }
            catch (RegexMatchTimeoutException)
            {
                // шаблон слишком тяжёлый для этого куска — считаем, что не подошёл
            }
        }

        return null;
    }

    private static string[] Keys(string tokens) => tokens.Split('+').Select(Normalize).Where(t => t.Length > 0).ToArray();

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
