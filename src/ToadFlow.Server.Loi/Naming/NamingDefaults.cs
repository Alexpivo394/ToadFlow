namespace ToadFlow.Server.Loi.Naming;

/// <summary>
///     Настройки именования на новом сервере: распространённые коды разделов (ГОСТ Р 21.101) латиницей и кириллицей, корпуса,
///     секции и версия Revit в имени. Объекты и заказчики у каждой компании свои — их добавляют в настройках.
/// </summary>
public static class NamingDefaults
{
    private static readonly (string Value, string Tokens)[] Disciplines =
    [
        ("АР", "AR"), ("КР", "KR"), ("КЖ", "KZH"), ("КЖ", "KJ"), ("КМ", "KM"), ("КД", "KD"),
        ("ОВ", "OV"), ("ВК", "VK"), ("НВК", "NVK"), ("ЭОМ", "EOM"), ("ЭМ", "EM"), ("ЭО", "EO"), ("СС", "SS"),
        ("ПТ", "PT"), ("АПТ", "APT"), ("ИТП", "ITP"), ("ТХ", "TH"), ("ТХ", "TX"), ("ГП", "GP"), ("ТМ", "TM")
    ];

    public static NamingConfig Create() => new()
    {
        Separators = "_",
        Entries = Disciplines.Select(d => new NamingEntry(NamingFields.Discipline, d.Tokens, d.Value)).ToList(),
        Patterns =
        [
            new NamingPattern(NamingFields.Part, @"^K(\d+)$", "корпус $1"),
            new NamingPattern(NamingFields.Part, @"^K(\d+)-K?(\d+)$", "корпуса $1–$2"),
            new NamingPattern(NamingFields.Part, @"^S(\d+)$", "секция $1"),
            new NamingPattern(NamingFields.Part, @"^S(\d+)-S?(\d+)$", "секции $1–$2"),
            new NamingPattern(NamingFields.Ignore, @"^R(20)?\d{2}$")
        ]
    };
}
