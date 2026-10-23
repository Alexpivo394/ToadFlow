using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace ToadFlow.Agent.Revit;

/// <param name="HasNwcExporter">По манифестам аддинов; точное значение сообщает аддин после запуска Revit.</param>
public sealed record RevitInstallation(int Year, string RevitExe, bool HasNwcExporter);

public sealed record UninstallEntry(string DisplayName, string? InstallLocation);

public interface IRegistryReader
{
    IEnumerable<UninstallEntry> UninstallEntries();
}

public interface IFileSystem
{
    bool FileExists(string path);
    IEnumerable<string> GetFiles(string directory, string pattern);
    string ReadAllText(string path);
}

/// <summary>
///     Установленные Revit 2020–2027: по записям «Программы и компоненты» с именем «Revit 20NN» (или «Autodesk Revit 20NN»),
///     где рядом лежит Revit.exe, и по стандартной папке %ProgramFiles%\Autodesk\Revit 20NN.
/// </summary>
public sealed partial class RevitLocator(IRegistryReader registry, IFileSystem files, string programFiles, string programData)
{
    public const int MinYear = 2020;
    public const int MaxYear = 2027;

    public RevitLocator() : this(new WindowsRegistryReader(), new DiskFileSystem(),
        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData))
    {
    }

    public IReadOnlyList<RevitInstallation> Find()
    {
        var exes = new SortedDictionary<int, string>();

        foreach (var entry in registry.UninstallEntries())
        {
            var match = ProductName().Match(entry.DisplayName.Trim());
            if (!match.Success || string.IsNullOrWhiteSpace(entry.InstallLocation)) continue;
            var year = int.Parse(match.Groups[1].Value);
            var exe = Path.Combine(entry.InstallLocation.Trim(), "Revit.exe");
            if (InRange(year) && !exes.ContainsKey(year) && files.FileExists(exe)) exes[year] = exe;
        }

        for (var year = MinYear; year <= MaxYear; year++)
        {
            var exe = Path.Combine(programFiles, "Autodesk", $"Revit {year}", "Revit.exe");
            if (!exes.ContainsKey(year) && files.FileExists(exe)) exes[year] = exe;
        }

        return exes.Select(pair => new RevitInstallation(pair.Key, pair.Value, HasNwcExporter(pair.Key))).ToList();
    }

    private bool HasNwcExporter(int year)
    {
        var addins = Path.Combine(programData, "Autodesk", "Revit", "Addins", year.ToString());
        return files.GetFiles(addins, "*.addin").Any(manifest =>
            Path.GetFileName(manifest).Contains("Navisworks", StringComparison.OrdinalIgnoreCase)
            || files.ReadAllText(manifest).Contains("Navisworks", StringComparison.OrdinalIgnoreCase));
    }

    private static bool InRange(int year) => year is >= MinYear and <= MaxYear;

    [GeneratedRegex(@"^(?:Autodesk )?Revit (20\d\d)$", RegexOptions.CultureInvariant)]
    private static partial Regex ProductName();
}

/// <summary>Записи «Программы и компоненты» из 64- и 32-битной ветки реестра машины.</summary>
public sealed class WindowsRegistryReader : IRegistryReader
{
    private static readonly string[] Roots =
    [
        @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall",
        @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall"
    ];

    public IEnumerable<UninstallEntry> UninstallEntries()
    {
        foreach (var root in Roots)
        {
            using var key = Registry.LocalMachine.OpenSubKey(root);
            if (key is null) continue;
            foreach (var name in key.GetSubKeyNames())
            {
                using var product = key.OpenSubKey(name);
                if (product?.GetValue("DisplayName") is string displayName)
                    yield return new UninstallEntry(displayName, product.GetValue("InstallLocation") as string);
            }
        }
    }
}

public sealed class DiskFileSystem : IFileSystem
{
    public bool FileExists(string path) => File.Exists(path);

    public IEnumerable<string> GetFiles(string directory, string pattern) =>
        Directory.Exists(directory) ? Directory.GetFiles(directory, pattern) : [];

    public string ReadAllText(string path)
    {
        try
        {
            return File.ReadAllText(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return "";
        }
    }
}
