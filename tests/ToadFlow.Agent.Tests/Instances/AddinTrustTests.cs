using System.Xml.Linq;
using Microsoft.Win32;
using Shouldly;
using ToadFlow.Agent.Instances;

namespace ToadFlow.Agent.Tests.Instances;

public class AddinTrustTests
{
    [Test]
    public void Ensure_WritesAlwaysLoad_ForThatYear()
    {
        var baseKey = $@"Software\ToadFlowTests\{Guid.NewGuid():N}";
        try
        {
            new AddinTrust(Registry.CurrentUser, baseKey).Ensure(2022);
            new AddinTrust(Registry.CurrentUser, baseKey).Ensure(2022); // повторно — без ошибок

            using var key = Registry.CurrentUser.OpenSubKey($@"{baseKey}\Autodesk Revit 2022\CodeSigning").ShouldNotBeNull();
            key.GetValue(AddinTrust.AddinId).ShouldBe(1);
            key.GetValueKind(AddinTrust.AddinId).ShouldBe(RegistryValueKind.DWord);
            Registry.CurrentUser.OpenSubKey($@"{baseKey}\Autodesk Revit 2024").ShouldBeNull();
        }
        finally
        {
            Registry.CurrentUser.DeleteSubKeyTree(baseKey, false);
        }
    }

    [Test]
    public void AddinId_MatchesManifest()
    {
        var manifest = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "src", "ToadFlow.Revit.Addin", "ToadFlow.Revit.Addin.addin");

        XDocument.Load(manifest).Descendants("AddInId").Single().Value.ToLowerInvariant().ShouldBe(AddinTrust.AddinId);
    }
}
