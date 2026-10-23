using Microsoft.Win32;

namespace ToadFlow.Agent.Instances;

/// <summary>
///     Помечает аддин ToadFlow доверенным («Всегда загружать») для Revit нужного года, чтобы при запуске не появлялось окно
///     «Неподписанная надстройка», которое некому нажать на машине с агентом. Revit хранит выбор в
///     HKCU\Software\Autodesk\Revit\Autodesk Revit {год}\CodeSigning: имя значения — AddInId из манифеста, 1 — всегда загружать.
/// </summary>
public sealed class AddinTrust(RegistryKey? root = null, string? baseKey = null)
{
    /// <summary>AddInId из ToadFlow.Revit.Addin.addin — менять только вместе с манифестом.</summary>
    public const string AddinId = "3c2f6e0b-7a44-4c1d-9b57-2f1d8c6a9e21";

    private const string DefaultBaseKey = @"Software\Autodesk\Revit";

    public void Ensure(int year)
    {
        using var key = (root ?? Registry.CurrentUser).CreateSubKey($@"{baseKey ?? DefaultBaseKey}\Autodesk Revit {year}\CodeSigning");
        if (key.GetValue(AddinId) is not 1) key.SetValue(AddinId, 1, RegistryValueKind.DWord);
    }
}
