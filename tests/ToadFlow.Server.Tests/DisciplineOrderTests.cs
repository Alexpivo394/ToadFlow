using Shouldly;
using ToadFlow.Server.Clash.Reports;
using ToadFlow.Server.Naming;

namespace ToadFlow.Server.Tests;

public class DisciplineOrderTests
{
    private static IEnumerable<string> Sorted(DisciplineRanks ranks, params string[] present) => present.OrderBy(ranks.Of);

    [Test]
    public void Order_FollowsNamingDefaults()
    {
        var ranks = DisciplineRanks.From(NamingDefaults.Create(), ["ВК", "АР", "ОВ", "КР"]);

        Sorted(ranks, "ВК", "АР", "ОВ", "КР").ShouldBe(["АР", "КР", "ОВ", "ВК"]);
    }

    [Test]
    public void Order_UnknownDiscipline_Last()
    {
        var ranks = DisciplineRanks.From(NamingDefaults.Create(), ["ОВ", "Ландшафт", "Благоустройство"]);

        Sorted(ranks, "Ландшафт", "ОВ", "Благоустройство").ShouldBe(["ОВ", "Благоустройство", "Ландшафт"]);
    }

    [Test]
    public void Label_InRankOrder()
    {
        var ranks = DisciplineRanks.From(NamingDefaults.Create(), ["ВК", "ОВ"]);

        ranks.Label("ВК", "ОВ").ShouldBe("ОВ × ВК");
        ranks.Label("ОВ", "ВК").ShouldBe("ОВ × ВК");
        ranks.Label("ОВ", "ОВ").ShouldBe("ОВ × ОВ");
        ranks.Ordered("ВК", "ОВ").ShouldBe(("ОВ", "ВК"));
        ranks.PairRank("ВК", "ОВ").ShouldBe(ranks.PairRank("ОВ", "ВК"));
    }

    [Test]
    public void Order_NamingSettingsChanged_UsesSettings()
    {
        var config = new NamingConfig
        {
            Entries =
            [
                new NamingEntry(NamingFields.Discipline, "VK", "ВК"),
                new NamingEntry(NamingFields.Discipline, "OV", "ОВ"),
                new NamingEntry(NamingFields.Discipline, "OV2", "ОВ"),
                new NamingEntry(NamingFields.Discipline, "AR")
            ]
        };

        var ranks = DisciplineRanks.From(config, ["ОВ", "ВК", "AR"]);

        Sorted(ranks, "ОВ", "AR", "ВК").ShouldBe(["ВК", "ОВ", "AR"]);
    }
}
