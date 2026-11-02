using Alexpivo394.Revit.Clash.Geometry;
using Alexpivo394.Revit.Clash.Revit;
using Shouldly;
using ToadFlow.Revit.Addin.Logic;

namespace ToadFlow.Revit.Addin.Tests;

public class ClashReportTests
{
    private static ExtractResult Result(int skipped, int far)
    {
        var header = new ModelHeader("m", "m.rvt", 2022, DateTimeOffset.UnixEpoch, true, "Navisworks", 0, 0, 0);
        var list = Enumerable.Range(1, skipped).Select(i => new SkippedElement($"u{i}", i, "геометрия", "сбой Revit")).ToList();
        return new ExtractResult(new ModelGeometry(header, [], []), new ModelParameters(new Dictionary<string, IReadOnlyList<ParameterValue>>()), list, far);
    }

    [Test]
    public void DescribeReport_Clean_NoLines() => ClashMapping.DescribeReport(Result(0, 0)).ShouldBeEmpty();

    [Test]
    public void DescribeReport_Skipped_CountAndFirst20()
    {
        var lines = ClashMapping.DescribeReport(Result(25, 0));

        lines.Count.ShouldBe(21);
        lines[0].ShouldBe("Пропущено элементов: 25");
        lines[1].ShouldBe("u1 (ID 1), геометрия: сбой Revit");
        lines[20].ShouldStartWith("u20 ");
    }

    [Test]
    public void DescribeReport_Far_Warning() =>
        ClashMapping.DescribeReport(Result(0, 3)).ShouldBe(
            ["3 элементов дальше 1 км от внутреннего начала — точность геометрии снижена. Перенесите модель ближе к внутреннему началу"]);
}
