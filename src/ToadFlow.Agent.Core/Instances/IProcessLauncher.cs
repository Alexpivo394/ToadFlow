using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace ToadFlow.Agent.Instances;

/// <summary>
///     Счётчики процесса Revit, снятые агентом независимо от аддина: по их приросту сторож зависаний отличает долгую
///     операцию в потоке Revit API от зависания, а агент — сколько памяти занимают экземпляры.
/// </summary>
/// <param name="CpuTime">Процессорное время всех потоков с запуска процесса.</param>
/// <param name="IoBytes">Байт прочитано, записано и прочих операций ввода-вывода с запуска процесса.</param>
public readonly record struct ProcessSample(TimeSpan CpuTime, ulong IoBytes, long WorkingSetMb, long PrivateMb);

/// <summary>Процесс Revit, которым управляет агент.</summary>
public interface IRevitProcess
{
    int Pid { get; }
    bool HasExited { get; }
    long WorkingSetMb { get; }

    /// <summary>Завершается, когда процесс закрылся — сам или убитый.</summary>
    Task Exited { get; }

    void Kill();

    /// <summary>Счётчики процесса сейчас; null — процесс закрылся или счётчики недоступны.</summary>
    ProcessSample? Sample() => null;

    /// <summary>Окна-диалоги этого процесса для <see cref="DialogWatcher" />.</summary>
    IRevitWindows Windows => NoRevitWindows.Instance;

    /// <summary>Для причины неудачного запуска: сессия Windows и журнал Revit этого процесса; null — сказать нечего.</summary>
    string? Describe() => null;
}

public interface IProcessLauncher
{
    IRevitProcess Start(int year, string exe, IReadOnlyDictionary<string, string> environment);
}

/// <summary>
///     Запуск настоящего Revit.exe с переменными окружения для аддина. Аддин помечается «Всегда загружать» в реестре;
///     Revit 2022 эту отметку не учитывает, и на окно «Надстройка без подписи» отвечает <see cref="DialogWatcher" />.
/// </summary>
public sealed partial class ProcessLauncher(AddinTrust trust) : IProcessLauncher
{
    public IRevitProcess Start(int year, string exe, IReadOnlyDictionary<string, string> environment)
    {
        trust.Ensure(year);
        var info = new ProcessStartInfo(exe, "/nosplash") { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(exe)! };
        foreach (var (name, value) in environment) info.Environment[name] = value;
        var process = Process.Start(info) ?? throw new InvalidOperationException($"Не удалось запустить {exe}");
        return new RevitProcess(process, year);
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetProcessIoCounters(SafeProcessHandle process, out IoCounters counters);

    [StructLayout(LayoutKind.Sequential)]
    private struct IoCounters
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    private sealed class RevitProcess : IRevitProcess
    {
        private const long Mb = 1024 * 1024;
        private readonly Process _process;
        private readonly int _year;
        private readonly DateTime _startedAt = DateTime.Now;

        public RevitProcess(Process process, int year)
        {
            _process = process;
            _year = year;
            _process.EnableRaisingEvents = true;
            Exited = _process.WaitForExitAsync();
            Windows = new RevitWindows(process.Id);
        }

        public int Pid => _process.Id;
        public bool HasExited => _process.HasExited;
        public Task Exited { get; }
        public IRevitWindows Windows { get; }

        public long WorkingSetMb
        {
            get
            {
                try
                {
                    _process.Refresh();
                    return _process.WorkingSet64 / Mb;
                }
                catch (InvalidOperationException)
                {
                    return 0;
                }
            }
        }

        public ProcessSample? Sample()
        {
            try
            {
                _process.Refresh();
                if (_process.HasExited) return null;
                var io = GetProcessIoCounters(_process.SafeHandle, out var counters)
                    ? counters.ReadTransferCount + counters.WriteTransferCount + counters.OtherTransferCount
                    : 0;
                return new ProcessSample(_process.TotalProcessorTime, io, _process.WorkingSet64 / Mb, _process.PrivateMemorySize64 / Mb);
            }
            catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                return null; // процесс закрылся между проверкой и чтением
            }
        }

        public string? Describe()
        {
            var parts = new[] { StartDiagnostics.Session(), StartDiagnostics.Journal(_year, _startedAt) is { } journal ? $"журнал Revit: {journal}" : null };
            var known = parts.OfType<string>().ToList();
            return known.Count == 0 ? null : string.Join(", ", known);
        }

        public void Kill()
        {
            try
            {
                if (!_process.HasExited) _process.Kill(entireProcessTree: true);
            }
            catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                // уже закрылся
            }
        }
    }
}
