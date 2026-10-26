using Shouldly;
using ToadFlow.Server.Loi.Naming;

namespace ToadFlow.Server.Tests;

/// <summary>Имена — настоящие, из проектов ГК «Пионер»: порядок кусков плавает, бывают пробелы и русские буквы среди латинских.</summary>
public class ModelNameParserTests
{
    private static readonly NamingConfig Pioneer = NamingDefaults.Create() with
    {
        Entries =
        [
            ..NamingDefaults.Create().Entries,
            new(NamingFields.Object, "POL", "Полковая", "ГК Пионер"),
            new(NamingFields.Object, "DON", "Донской", "ГК Пионер"),
            new(NamingFields.Object, "DON+BNK-DOO", "Донской БНК-ДОО", "ГК Пионер"),
            new(NamingFields.Object, "ROD", "Родина", "ГК Пионер"),
            new(NamingFields.Discipline, "ARF", "АР"),
            new(NamingFields.Discipline, "Fc", "АР"),
            new(NamingFields.Discipline, "OVO", "ОВ (отопление)"),
            new(NamingFields.Discipline, "OVV", "ОВ (вентиляция)"),
            new(NamingFields.Discipline, "OVK", "ОВ (кондиционирование)"),
            new(NamingFields.Part, "St", "стилобат"),
            new(NamingFields.Part, "STLB", "стилобат"),
            new(NamingFields.Part, "PRK", "стилобат"),
            new(NamingFields.Ignore, "RLP"),
            new(NamingFields.Ignore, "SOB"),
            new(NamingFields.Ignore, "ES"),
            new(NamingFields.Ignore, "RD")
        ]
    };

    private static ModelClassification Parse(string name) => new ModelNameParser(Pioneer).Parse(name);

    [Test]
    [Arguments("POL_EOM_RLP_S1-S2_R22", "Полковая", "ЭОМ", "секции 1–2")]
    [Arguments("POL_OVV_RLP_St_R22", "Полковая", "ОВ (вентиляция)", "стилобат")]
    [Arguments("POL_PT_ RLP_R22", "Полковая", "ПТ", null)]
    [Arguments("POL_VK_ RLP_St_R22", "Полковая", "ВК", "стилобат")]
    [Arguments("POL_AR_RLP_Fc_R22", "Полковая", "АР", null)]
    [Arguments("DON_EOM_SOB_K1-K5_ES_R22", "Донской", "ЭОМ", "корпуса 1–5")]
    [Arguments("DON_ARF_SOB_BNK-DOO_R22", "Донской БНК-ДОО", "АР", null)]
    [Arguments("DON_KR_SOB_STLB_R22", "Донской", "КР", "стилобат")]
    [Arguments("ROD_AR_K1_Fc_R22", "Родина", "АР", "корпус 1")]
    [Arguments("ROD_AR_K1-К3_R22", "Родина", "АР", "корпуса 1–3")]
    [Arguments("ROD_RD_KR_R22", "Родина", "КР", null)]
    [Arguments("RSN://srv-rs22/Пионер/POL/POL_SS_ RLP_R22.rvt", "Полковая", "СС", null)]
    public void PioneerNames(string name, string project, string discipline, string? part)
    {
        var result = Parse(name);

        result.Customer.ShouldBe("ГК Пионер");
        result.Object.ShouldBe(project);
        result.Discipline.ShouldBe(discipline);
        result.Part.ShouldBe(part);
        result.Unrecognized.ShouldBeEmpty();
    }

    [Test]
    [Arguments("POL_АР_K1_R22", "АР", "корпус 1")]
    [Arguments("POL_ОВ_С3_R22", "ОВ", "секция 3")]
    [Arguments("POL_КЖ_КОРП2_R22", "КЖ", "корпус 2")]
    [Arguments("POL_ТХ_СЕК4-5_R22", "ТХ", "секции 4–5")]
    [Arguments("POL_ЭОМ_Б2_R22", "ЭОМ", "блок 2")]
    public void CyrillicCodes_MatchLatinDictionary(string name, string discipline, string part)
    {
        var result = Parse(name);

        result.Object.ShouldBe("Полковая");
        result.Discipline.ShouldBe(discipline);
        result.Part.ShouldBe(part);
        result.Unrecognized.ShouldBeEmpty();
    }

    [Test]
    public void CyrillicLookalike_InLatinToken_IsATypo()
    {
        // «РOL» — русская Р среди латиницы: это опечатка в POL, а не транслитерация «ROL».
        Parse("РOL_AR_R22").Object.ShouldBe("Полковая");
    }

    [Test]
    public void CyrillicEntry_MatchesLatinName()
    {
        var config = NamingDefaults.Create() with { Entries = [new(NamingFields.Object, "ЖК+Победа", "Победа")] };

        new ModelNameParser(config).Parse("ZHK_POBEDA_AR").Object.ShouldBe("Победа");
    }

    [Test]
    public void Defaults_RecognizeStageServicePiecesDatesAndVersions()
    {
        var result = new ModelNameParser(NamingDefaults.Create()).Parse("XXX_AR_RD_STIL_251026_V3_detached");

        result.Discipline.ShouldBe("АР");
        result.Part.ShouldBe("стилобат");
        result.Fields.ShouldBe(new Dictionary<string, string> { [NamingDefaults.StageField] = "Р" });
        result.Unrecognized.ShouldBe(["XXX"]);
        new ModelNameParser(NamingDefaults.Create()).Parse("XXX_AR_Р").Fields[NamingDefaults.StageField].ShouldBe("Р"); // русская Р — стадия Р, не P
    }

    [Test]
    public void Defaults_AreValid() => ModelNameParser.Validate(NamingDefaults.Create()).ShouldBeEmpty();

    [Test]
    public void UnknownPieces_AreReported()
    {
        var result = Parse("NEW_AR_XYZ_K2_R24");

        result.Object.ShouldBeNull();
        result.Discipline.ShouldBe("АР");
        result.Part.ShouldBe("корпус 2");
        result.Unrecognized.ShouldBe(["NEW", "XYZ"]);
    }

    [Test]
    public void CustomField_FromDictionary()
    {
        var config = Pioneer with { Entries = [..Pioneer.Entries, new("Стадия", "P", "П"), new("Стадия", "R", "Р")] };

        new ModelNameParser(config).Parse("POL_AR_P_R22").Fields.ShouldBe(new Dictionary<string, string> { ["Стадия"] = "П" });
    }

    [Test]
    public void NameExpression_ForStrictStandards_ValuesGoThroughDictionary()
    {
        var config = Pioneer with { NameExpressions = [@"^(?<object>[A-Z]+)-(?<discipline>[A-Z]+)-(?<part>.+)$"] };

        var result = new ModelNameParser(config).Parse("ROD-AR-Корпус 1");

        result.Object.ShouldBe("Родина");
        result.Customer.ShouldBe("ГК Пионер");
        result.Discipline.ShouldBe("АР");
        result.Part.ShouldBe("Корпус 1");
    }

    [Test]
    public void Validate_ReportsBrokenSettings()
    {
        var errors = ModelNameParser.Validate(new NamingConfig
        {
            Separators = "",
            Entries = [new(NamingFields.Discipline, "AR"), new(NamingFields.Object, "", "Без кусков")],
            Patterns = [new(NamingFields.Part, "K(\\d+", "корпус $1")]
        });

        errors.ShouldContain(e => e.Contains("разделитель"));
        errors.ShouldContain(e => e.Contains("не указано значение"));
        errors.ShouldContain(e => e.Contains("куски имени"));
        errors.ShouldContain(e => e.Contains("регулярное выражение"));
    }
}
