using System.Text.Json;
using System.Text.Json.Serialization;
using Alexpivo394.Revit.Families.Files;
using Shouldly;
using ToadFlow.Contracts;
using ToadFlow.Contracts.Families;
using ToadFlow.Revit.Addin.Logic;

namespace ToadFlow.Revit.Addin.Tests;

public class FamilyReadLogicTests
{
    private static readonly JsonSerializerOptions Server = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    [Test]
    public void HashMismatch_GivesWarningOutcome()
    {
        var hash = new string('a', 64);

        FamilyReadLogic.CheckHash(hash, hash).ShouldBeNull();
        FamilyReadLogic.CheckHash(hash, new string('b', 64)).ShouldBe((JobOutcome.Warning, FamilyReadLogic.ChangedNote));
    }

    [Test]
    public void OversizedPreview_RetriedSmaller_ThenDropped()
    {
        var asked = new List<int>();
        byte[]? Render(int pixels, int bytes)
        {
            asked.Add(pixels);
            return new byte[bytes];
        }

        FamilyReadLogic.Preview(pixels => Render(pixels, 10_000)).ShouldNotBeNull().Length.ShouldBe(10_000);
        asked.ShouldBe([256]);

        asked.Clear();
        FamilyReadLogic.Preview(pixels => Render(pixels, pixels == 256 ? 200_000 : 60_000)).ShouldNotBeNull().Length.ShouldBe(60_000);
        asked.ShouldBe([256, 128]);

        asked.Clear();
        FamilyReadLogic.Preview(pixels => Render(pixels, 200_000)).ShouldBeNull();
        asked.ShouldBe([256, 128]);

        FamilyReadLogic.Preview(_ => null).ShouldBeNull();
        FamilyReadLogic.Preview(_ => throw new InvalidOperationException("вид не печатается")).ShouldBeNull();
    }

    [Test]
    public void Payload_ReadsServerJson()
    {
        var payload = new FamilyReadPayload(Guid.NewGuid(), @"\\nas\Families\ОВ\Клапан.rfa", new string('a', 64), @"\\nas\Families\ОВ\Клапан.txt");

        FamilyReadLogic.ParsePayload(JsonSerializer.Serialize(payload, Server)).ShouldBe(payload);
    }

    [Test]
    public void Result_KeepsParameterNames_AndIsReadByServer()
    {
        var content = new FamilyContent("Арматура трубопроводов", "OneLevelBased", 2022,
            [new FamilyTypeInfo("DN50", new Dictionary<string, string> { ["Width"] = "50 мм", ["Обозначение"] = "КЛАБ-40-20" })],
            [new FamilyParameterInfo("Width", true, Guid.NewGuid(), false, "Размеры", "Длина")], true);

        var json = FamilyReadLogic.SerializeResult(new FamilyReadResult(content, "iVBORw0KGgo="));

        json.ShouldContain("\"Width\":");
        var read = JsonSerializer.Deserialize<FamilyReadResult>(json, Server).ShouldNotBeNull();
        read.PreviewPngBase64.ShouldBe("iVBORw0KGgo=");
        read.Content.Types.ShouldHaveSingleItem().Values["Width"].ShouldBe("50 мм");
        read.Content.Parameters.ShouldHaveSingleItem().SharedGuid.ShouldBe(content.Parameters[0].SharedGuid);
        (read.Content.Category, read.Content.Placement, read.Content.RevitYear, read.Content.TypesFromCatalog).ShouldBe(("Арматура трубопроводов", "OneLevelBased", 2022, true));
    }

    [Test]
    public void OpenFailure_TextFromRevit()
    {
        var (outcome, note) = FamilyReadLogic.MapFailure(new InvalidOperationException("The file is corrupt."));

        outcome.ShouldBe(JobOutcome.Failed);
        note.ShouldBe("Revit не открыл файл: The file is corrupt.");
        FamilyReadLogic.MapFailure(new FileNotFoundException("нет")).ShouldBe((JobOutcome.Warning, FamilyReadLogic.ChangedNote));
        FamilyReadLogic.MapFailure(new IOException("сеть")).Outcome.ShouldBe(JobOutcome.InfrastructureFailure);
    }
}
