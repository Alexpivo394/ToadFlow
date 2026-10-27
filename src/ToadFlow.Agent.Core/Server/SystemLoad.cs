using System.Runtime.InteropServices;

namespace ToadFlow.Agent.Server;

/// <summary>Физическая память машины, МБ.</summary>
public readonly record struct RamSnapshot(long TotalMb, long AvailableMb);

/// <summary>Сколько физической памяти свободно — для решения, хватит ли её на ещё один Revit.</summary>
public interface ISystemMemory
{
    /// <summary>null — ОС не ответила.</summary>
    RamSnapshot? Snapshot();
}

/// <summary>Загрузка процессора и память машины — для дашборда и контроля памяти.</summary>
internal sealed partial class SystemLoad : ISystemMemory
{
    private ulong _lastIdle;
    private ulong _lastTotal;

    /// <summary>Процент загрузки процессора с прошлого вызова.</summary>
    public double CpuPercent()
    {
        if (!GetSystemTimes(out var idle, out var kernel, out var user)) return 0;
        var total = kernel + user; // время ядра уже включает простой
        var idleDelta = idle - _lastIdle;
        var totalDelta = total - _lastTotal;
        var first = _lastTotal == 0;
        _lastIdle = idle;
        _lastTotal = total;
        return first || totalDelta == 0 ? 0 : Math.Round(100.0 * (totalDelta - idleDelta) / totalDelta, 1);
    }

    public long RamUsedMb() => Snapshot() is { } ram ? ram.TotalMb - ram.AvailableMb : 0;

    public RamSnapshot? Snapshot()
    {
        var status = new MemoryStatus { Length = (uint)Marshal.SizeOf<MemoryStatus>() };
        return GlobalMemoryStatusEx(ref status) ? new RamSnapshot((long)(status.TotalPhys / (1024 * 1024)), (long)(status.AvailPhys / (1024 * 1024))) : null;
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetSystemTimes(out ulong idle, out ulong kernel, out ulong user);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GlobalMemoryStatusEx(ref MemoryStatus buffer);

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatus
    {
        public uint Length;
        public uint MemoryLoad;
        public ulong TotalPhys;
        public ulong AvailPhys;
        public ulong TotalPageFile;
        public ulong AvailPageFile;
        public ulong TotalVirtual;
        public ulong AvailVirtual;
        public ulong AvailExtendedVirtual;
    }
}
