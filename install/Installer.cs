using System.Text.RegularExpressions;
using WixSharp;
using File = WixSharp.File;

// Аргументы: <версия> <папка опубликованного агента> <папки bin\Release.R20\publish … bin\Release.R27\publish>
if (args.Length < 3)
{
    Console.Error.WriteLine("Использование: Installer <версия> <папка агента> <папки publish аддина…>");
    return 2;
}

var version = Version.Parse(args[0].Split('-')[0]);
var agentFolder = args[1];
const string agentExe = "ToadFlow.Agent.exe";

var agentExeId = new Id("AgentExe");
var agentFiles = new Dir(new Id("INSTALLDIR"), @"%ProgramFiles%\ToadFlow\Agent",
    new File(agentExeId, Path.Combine(agentFolder, agentExe)),
    new Files(Path.Combine(agentFolder, "*.*"), path => !path.EndsWith(agentExe, StringComparison.OrdinalIgnoreCase)));
var startMenu = new Dir(@"%ProgramMenu%\ToadFlow",
    new ExeFileShortcut("ToadFlow Agent", $"[INSTALLDIR]{agentExe}", "") { WorkingDirectory = "INSTALLDIR" });

// Аддин: манифест и папка для каждого года. До 2027 — общая папка аддинов в ProgramData, с 2027 — Program Files (как у Autodesk).
var addinDirs = new List<WixEntity>();
foreach (var publish in args[2..])
{
    var year = int.Parse(Regex.Match(Path.GetFullPath(publish), @"R(\d{2})[\\/]publish", RegexOptions.IgnoreCase).Groups[1].Value) + 2000;
    var root = year >= 2027 ? @"%ProgramFiles%\Autodesk\Revit\Addins" : @"%CommonAppDataFolder%\Autodesk\Revit\Addins";
    addinDirs.Add(new Dir($@"{root}\{year}", new Files(Path.Combine(publish, "*.*"))));
    Console.WriteLine($"Аддин для Revit {year}: {publish}");
}

// Регистрация после установки, если заданы SERVER и CODE: msiexec /i … SERVER=http://toadflow:8080 CODE=TF-XXXX-XXXX [TRUSTADDIN=1].
// Неверный код не валит установку: агент покажет окно подключения при первом запуске.
const string register = "--register --server \"[SERVER]\" --code \"[CODE]\"";
var registration = new[]
{
    new InstalledFileAction(agentExeId, register, Return.ignore, When.After, Step.InstallFiles,
        new Condition("SERVER AND CODE AND TRUSTADDIN<>\"1\" AND NOT REMOVE"))
    {
        Execute = Execute.deferred,
        Impersonate = false
    },
    new InstalledFileAction(agentExeId, register + " --trust-addin", Return.ignore, When.After, Step.InstallFiles,
        new Condition("SERVER AND CODE AND TRUSTADDIN=\"1\" AND NOT REMOVE"))
    {
        Execute = Execute.deferred,
        Impersonate = false
    }
};

var project = new Project("ToadFlow Agent", [agentFiles, startMenu, .. addinDirs, .. registration])
{
    GUID = new Guid("5E1A4B2D-8C73-4F0A-9D61-3B7E2C9F4A18"),
    Version = version,
    Platform = Platform.x64,
    Scope = InstallScope.perMachine,
    MajorUpgrade = MajorUpgrade.Default,
    UI = WUI.WixUI_ProgressOnly,
    OutDir = "output",
    OutFileName = $"ToadFlowAgent-{args[0]}",
    // Значок в «Приложения» Windows; ярлык в «Пуске» берёт значок самого exe агента.
    ControlPanelInfo =
    {
        Manufacturer = "ToadFlow",
        UrlInfoAbout = "https://github.com/Alexpivo394/ToadFlow",
        ProductIcon = @"src\ToadFlow.Agent\Resources\toadflow.ico"
    },
    Properties =
    [
        new Property("SERVER", ""),
        new Property("CODE", ""),
        new Property("TRUSTADDIN", "")
    ],
    // Агент работает в сессии пользователя (Revit не запускается службой): автозапуск при входе любого пользователя.
    RegValues =
    [
        new RegValue(RegistryHive.LocalMachine, @"Software\Microsoft\Windows\CurrentVersion\Run", "ToadFlowAgent",
            $"\"[INSTALLDIR]{agentExe}\" --autostart") { Win64 = true }
    ]
};

project.ResolveWildCards();
var msi = project.BuildMsi();
return msi is null ? 1 : 0;
