namespace ToadFlow.Server.Loi.Naming;

/// <summary>
///     Стандартный справочник именования: коды разделов (ГОСТ Р 21.101 и распространённые английские), стилобат и паркинг,
///     стадия, служебные куски Revit; шаблоны корпусов, секций, блоков, версии Revit, дат и версий файла. Коды достаточно
///     записать латиницей — набранные по-русски (<c>АР</c>, <c>ОВ</c>, <c>КЖ</c>) парсер сравнивает с ними сам. Объекты и
///     заказчики у каждой компании свои — их добавляют в настройках.
/// </summary>
public static class NamingDefaults
{
    public const string StageField = "Стадия";

    private static readonly (string Value, string[] Tokens)[] Disciplines =
    [
        ("АР", ["AR", "ARCH"]),
        ("АС", ["AS"]),
        ("КР", ["KR", "STR"]),
        ("КЖ", ["KZH", "KJ"]),
        ("КЖИ", ["KZHI"]),
        ("КМ", ["KM"]),
        ("КМД", ["KMD"]),
        ("КД", ["KD"]),
        ("ОВ", ["OV", "HVAC"]),
        ("ОВиК", ["OVIK"]),
        ("ВК", ["VK"]),
        ("НВК", ["NVK"]),
        ("НВ", ["NV"]),
        ("НК", ["NK"]),
        ("ЭОМ", ["EOM"]),
        ("ЭМ", ["EM"]),
        ("ЭО", ["EO"]),
        ("ЭН", ["EN"]),
        ("ЭС", ["ES"]),
        ("СС", ["SS"]),
        ("СКС", ["SKS"]),
        ("АПС", ["APS"]),
        ("СОУЭ", ["SOUE"]),
        ("ПТ", ["PT"]),
        ("АПТ", ["APT"]),
        ("АУПТ", ["AUPT"]),
        ("АОВ", ["AOV"]),
        ("АВК", ["AVK"]),
        ("ИТП", ["ITP"]),
        ("ТМ", ["TM"]),
        ("ТС", ["TS"]),
        ("ТХ", ["TH", "TX"]),
        ("ГП", ["GP"]),
        ("ГСН", ["GSN"]),
        ("ГСВ", ["GSV"])
    ];

    private static readonly (string Value, string[] Tokens)[] Parts =
    [
        ("стилобат", ["STIL", "STILOBAT"]),
        ("паркинг", ["PARK", "PARKING"])
    ];

    private static readonly (string Value, string[] Tokens)[] Stages =
    [
        ("П", ["P", "PD"]),
        ("Р", ["R", "RD"])
    ];

    /// <summary>Служебные куски: так Revit и пользователи помечают копии файла.</summary>
    private static readonly string[] Ignored = ["CENTRAL", "LOCAL", "DETACHED", "BACKUP"];

    public static NamingConfig Create() => new()
    {
        Separators = "_",
        Entries =
        [
            ..Disciplines.SelectMany(d => d.Tokens.Select(t => new NamingEntry(NamingFields.Discipline, t, d.Value))),
            ..Parts.SelectMany(p => p.Tokens.Select(t => new NamingEntry(NamingFields.Part, t, p.Value))),
            ..Stages.SelectMany(s => s.Tokens.Select(t => new NamingEntry(StageField, t, s.Value))),
            ..Ignored.Select(t => new NamingEntry(NamingFields.Ignore, t))
        ],
        Patterns =
        [
            new NamingPattern(NamingFields.Part, @"^(K|KORP|KOR)(\d+)$", "корпус $2"),
            new NamingPattern(NamingFields.Part, @"^(K|KORP|KOR)(\d+)-(K|KORP|KOR)?(\d+)$", "корпуса $2–$4"),
            new NamingPattern(NamingFields.Part, @"^(S|SEK|SEC|SEKTS)(\d+)$", "секция $2"),
            new NamingPattern(NamingFields.Part, @"^(S|SEK|SEC|SEKTS)(\d+)-(S|SEK|SEC|SEKTS)?(\d+)$", "секции $2–$4"),
            new NamingPattern(NamingFields.Part, @"^(B|BL|BLOK)(\d+)$", "блок $2"),
            new NamingPattern(NamingFields.Ignore, @"^R(20)?\d{2}$"),
            new NamingPattern(NamingFields.Ignore, @"^(\d{6}|\d{8})$"),
            new NamingPattern(NamingFields.Ignore, @"^V\d+$")
        ]
    };
}
