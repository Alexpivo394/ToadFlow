using Alexpivo394.Revit.Clash.Rules;
using Shouldly;
using ToadFlow.Server.Clash.Parts;

namespace ToadFlow.Server.Tests;

/// <summary>Отличия двух конфигураций части — готовые строки для человека: «Что изменилось» в версиях и в диалоге утверждения.</summary>
public class ClashConfigurationDiffTests
{
    private static readonly Guid Ar = Guid.NewGuid(), Kr = Guid.NewGuid(), Ov = Guid.NewGuid();
    private static readonly Guid Parking = Guid.NewGuid(), Walls = Guid.NewGuid(), Ducts = Guid.NewGuid();

    private static ConditionGroup Category(string value) =>
        new([new Condition(new PropertyRef(PropertyKind.Category), ConditionOperator.Equals, value)]);

    private static ClashConfiguration Config(double tolerance = 20, string parkingName = "Паркинг", IReadOnlyList<ConditionGroup>? parking = null,
        IReadOnlyList<SetPairExclusion>? exclusions = null) => new(
        [new SetGroup(Ar, "АР", GroupSource.OfDiscipline("АР")), new SetGroup(Kr, "КР", GroupSource.OfDiscipline("КР")),
            new SetGroup(Ov, "ОВ", GroupSource.OfDiscipline("ОВ"))],
        [new SelectionSet(Parking, Ar, parkingName, parking ?? [Category("Парковка")]), new SelectionSet(Walls, Kr, "Стены", [Category("Стены")]),
            new SelectionSet(Ducts, Ov, "Воздуховоды", [Category("Воздуховоды")])],
        [new MatrixCell(Ar, Kr, true, tolerance), new MatrixCell(Kr, Ov, true)],
        exclusions ?? []);

    [Test]
    public void Diff_CellTolerance()
    {
        var changes = ConfigurationDiff.Compare(Config(), Config(tolerance: 50));

        changes.ShouldHaveSingleItem().ShouldBe(new ConfigChange(ConfigChangeKind.CellTolerance, "Клетка АР × КР: допуск 20 → 50 мм"));
    }

    [Test]
    public void Diff_SetRenamedAndConditions()
    {
        var changes = ConfigurationDiff.Compare(Config(), Config(parkingName: "Паркинг 2", parking: [Category("Парковка"), Category("Пандусы")]));

        changes.ShouldBe([
            new ConfigChange(ConfigChangeKind.SetRenamed, "Набор «Паркинг» (АР) переименован в «Паркинг 2»"),
            new ConfigChange(ConfigChangeKind.SetConditions, "Набор «Паркинг 2» (АР): Категория = Парковка → Категория = Парковка или Категория = Пандусы")
        ]);
    }

    [Test]
    public void Diff_ExclusionAdded()
    {
        var changes = ConfigurationDiff.Compare(Config(), Config(exclusions: [new SetPairExclusion(Ducts, Walls)]));

        changes.ShouldHaveSingleItem().ShouldBe(new ConfigChange(ConfigChangeKind.ExclusionAdded, "Исключение «Воздуховоды» × «Стены» добавлено"));
        ConfigurationDiff.Compare(Config(exclusions: [new SetPairExclusion(Walls, Ducts)]), Config(exclusions: [new SetPairExclusion(Ducts, Walls)]))
            .ShouldBeEmpty(); // порядок наборов в исключении не важен
    }

    [Test]
    public void Diff_GroupExclusionAddedAndRemoved()
    {
        var with = Config() with { GroupExclusions = [new SetGroupExclusion(Ducts, Ar)] };

        ConfigurationDiff.Compare(Config(), with).ShouldHaveSingleItem()
            .ShouldBe(new ConfigChange(ConfigChangeKind.ExclusionAdded, "Исключение «Воздуховоды» × вся группа «АР» добавлено"));
        ConfigurationDiff.Compare(with, Config()).ShouldHaveSingleItem()
            .ShouldBe(new ConfigChange(ConfigChangeKind.ExclusionRemoved, "Исключение «Воздуховоды» × вся группа «АР» удалено"));
    }

    [Test]
    public void CopyFor_GroupExclusionFollowsNewSetIds()
    {
        var source = Config() with { GroupExclusions = [new SetGroupExclusion(Ducts, Ar), new SetGroupExclusion(Walls, Guid.NewGuid())] };

        var copy = DraftEndpoints.CopyFor(source, ["АР", "КР", "ОВ"], []);

        var ducts = copy.Sets.Single(s => s.Name == "Воздуховоды").Id;
        copy.GroupExclusions.ShouldBe([new SetGroupExclusion(ducts, Ar)]); // исключение с группой, которой нет, отброшено
    }

    [Test]
    public void WithoutRemovedModels_DropsGroupExclusionsOfGoneSetsAndGroups()
    {
        // Группу раздела сервер узнаёт по Id из имени раздела.
        var ar = ConfigurationDefaults.DisciplineGroup("АР");
        var ov = ConfigurationDefaults.DisciplineGroup("ОВ");
        var config = new ClashConfiguration([ar, ov],
            [new SelectionSet(Walls, ar.Id, "Стены", [Category("Стены")]), new SelectionSet(Ducts, ov.Id, "Воздуховоды", [Category("Воздуховоды")]),
                new SelectionSet(Parking, ar.Id, "Паркинг", [Category("Парковка")])], [], [])
        {
            GroupExclusions = [new SetGroupExclusion(Ducts, ar.Id), new SetGroupExclusion(Parking, ov.Id), new SetGroupExclusion(Walls, ar.Id)]
        };

        var result = PartEndpoints.WithoutRemovedModels(config, ["АР"], [])!;

        result.GroupExclusions.ShouldBe([new SetGroupExclusion(Walls, ar.Id)]);
    }

    [Test]
    public void Diff_Same_Empty() => ConfigurationDiff.Compare(Config(), Config()).ShouldBeEmpty();

    private static ClashConfiguration WithGroup(string name, IReadOnlyList<string> disciplines, IReadOnlyList<string> models) =>
        Config() with { Groups = [.. Config().Groups, new SetGroup(Driveways, name, new GroupSource(disciplines, models))] };

    private static readonly Guid Driveways = Guid.NewGuid();

    private static readonly Dictionary<string, string> ModelNames = new() { ["k1"] = "АР_корпус1.rvt", ["k2"] = "АР_корпус2.rvt", ["k3"] = "АР_паркинг.rvt" };

    /// <summary>Замена модели в группе видна по именам: число моделей то же, а состав другой.</summary>
    [Test]
    public void Diff_GroupModelReplaced_ByName()
    {
        var changes = ConfigurationDiff.Compare(WithGroup("Проезды", [], ["k1", "k2"]), WithGroup("Проезды", [], ["k1", "k3"]), ModelNames);

        changes.ShouldHaveSingleItem().ShouldBe(new ConfigChange(ConfigChangeKind.GroupSource,
            "Группа «Проезды»: модели добавлены: «АР_паркинг.rvt»; модели убраны: «АР_корпус2.rvt»"));
        ConfigurationDiff.Compare(Config(), WithGroup("Проезды", ["АР"], ["k3"]), ModelNames).ShouldHaveSingleItem().Text
            .ShouldBe("Группа «Проезды» добавлена: разделы АР; модели «АР_паркинг.rvt»");
    }

    /// <summary>Порядок разделов и моделей в источнике — не изменение; разделы — без учёта регистра, как в правилах.</summary>
    [Test]
    public void Diff_GroupSourceReordered_Empty() =>
        ConfigurationDiff.Compare(WithGroup("Проезды", ["АР", "КР"], ["k1", "k2"]), WithGroup("Проезды", ["кр", "АР"], ["k2", "k1"]), ModelNames)
            .ShouldBeEmpty();

    [Test]
    public void Diff_GroupDisciplines()
    {
        var changes = ConfigurationDiff.Compare(WithGroup("Проезды", ["АР"], []), WithGroup("Проезды", ["АР", "КР"], []), ModelNames);

        changes.ShouldHaveSingleItem().ShouldBe(new ConfigChange(ConfigChangeKind.GroupSource, "Группа «Проезды»: разделы АР → АР, КР"));
    }

    [Test]
    public void Diff_GroupRenamed()
    {
        var changes = ConfigurationDiff.Compare(WithGroup("Проезды", ["АР"], []), WithGroup("Пандусы", ["АР"], []));

        changes.ShouldHaveSingleItem().ShouldBe(new ConfigChange(ConfigChangeKind.GroupRenamed, "Группа «Проезды» переименована в «Пандусы»"));
    }

    [Test]
    public void Diff_SetsAndCells_AddedRemoved()
    {
        var before = Config();
        var after = before with
        {
            Sets = [.. before.Sets.Where(s => s.Id != Walls), new SelectionSet(Guid.NewGuid(), Kr, "Колонны", [Category("Колонны")])],
            Matrix = [new MatrixCell(Ar, Kr, false), new MatrixCell(Kr, Ov, true), new MatrixCell(Ar, Ov, true, 30)]
        };

        ConfigurationDiff.Compare(before, after).Select(c => c.Text).ShouldBe([
            "Набор «Колонны» (КР) добавлен",
            "Набор «Стены» (КР) удалён",
            "Клетка АР × КР выключена",
            "Клетка АР × ОВ включена, допуск 30 мм"
        ]);
    }

    [Test]
    public void Diff_ModelCondition_NamedByModel()
    {
        var first = Guid.NewGuid().ToString("N");
        var second = Guid.NewGuid().ToString("N");
        var byFile = new Condition(new PropertyRef(PropertyKind.Model), ConditionOperator.OneOf, null, [first, second]);
        var after = Config(parking: [new ConditionGroup([byFile])]);

        ConfigurationDiff.ModelKeys(Config(), after).ShouldBe([first, second]);
        ConfigurationDiff.Compare(Config(), after, new Dictionary<string, string> { [first] = "AR_1.rvt", [second] = "AR_2.rvt" }).ShouldBe([
            new ConfigChange(ConfigChangeKind.SetConditions, "Набор «Паркинг» (АР): Категория = Парковка → Модель — одно из: AR_1.rvt, AR_2.rvt")
        ]);
    }
}
