using System.Runtime.InteropServices;

namespace ToadFlow.Agent.Instances;

/// <summary>
///     Окружение запуска Revit для журналов — только чтение, ничего не настраивает: сессия Windows агента (консоль или RDP,
///     отключена, заблокирована) и журнал Revit, начатый этим процессом. Журнал называет окна, на которых Revit остановился.
/// </summary>
internal static partial class StartDiagnostics
{
    private const int CurrentSession = -1;
    private const int ConnectStateInfo = 8;
    private const int ClientProtocolInfo = 16;
    private const int SessionInfoEx = 25;
    private const int ProtocolConsole = 0;
    private const int ProtocolRdp = 2;
    private const int StateActive = 0;
    private const int StateDisconnected = 4;
    private const int SessionLocked = 0;

    /// <summary>Смещение SessionFlags в WTSINFOEXW: Level, выравнивание объединения по 8 байт, SessionId, SessionState.</summary>
    private const int SessionFlagsOffset = 16;

    /// <summary>«сессия Windows: консоль» и т. п.; null — Windows не ответила.</summary>
    public static string? Session()
    {
        if (QueryInt(ConnectStateInfo, 0) is not { } state || QueryInt(ClientProtocolInfo, 0, 2) is not { } protocol) return null;

        var parts = new List<string>
        {
            protocol switch
            {
                ProtocolConsole => "консоль",
                ProtocolRdp => "RDP",
                _ => $"протокол {protocol}"
            }
        };
        if (state == StateDisconnected) parts.Add("отключена");
        else if (state != StateActive) parts.Add($"состояние {state}");
        if (QueryInt(SessionInfoEx, SessionFlagsOffset) == SessionLocked) parts.Add("заблокирована");
        return $"сессия Windows: {string.Join(", ", parts)}";
    }

    /// <summary>Самый новый журнал Revit этого года, начатый не раньше <paramref name="startedAt" />; null — не найден.</summary>
    public static string? Journal(int year, DateTime startedAt)
    {
        var folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Autodesk", "Revit", $"Autodesk Revit {year}", "Journals");
        try
        {
            return new DirectoryInfo(folder).EnumerateFiles("journal.*.txt")
                .Where(file => file.CreationTimeUtc >= startedAt.ToUniversalTime().AddSeconds(-5))
                .MaxBy(file => file.CreationTimeUtc)?.FullName;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static int? QueryInt(int infoClass, int offset, int size = 4)
    {
        if (!WTSQuerySessionInformation(0, CurrentSession, infoClass, out var buffer, out var bytes)) return null;
        try
        {
            if (bytes < offset + size) return null;
            return size == 2 ? Marshal.ReadInt16(buffer, offset) : Marshal.ReadInt32(buffer, offset);
        }
        finally
        {
            WTSFreeMemory(buffer);
        }
    }

    [LibraryImport("wtsapi32.dll", EntryPoint = "WTSQuerySessionInformationW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool WTSQuerySessionInformation(nint server, int sessionId, int infoClass, out nint buffer, out int bytes);

    [LibraryImport("wtsapi32.dll")]
    private static partial void WTSFreeMemory(nint memory);
}
