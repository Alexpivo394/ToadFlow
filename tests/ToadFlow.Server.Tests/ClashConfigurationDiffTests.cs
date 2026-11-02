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
    public void Diff_Same_Empty() => ConfigurationDiff.Compare(Config(), Config()).ShouldBeEmpty();

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
}
