using System.Diagnostics;

namespace ToadFlow.Agent.Instances;

/// <summary>Процесс Revit, которым управляет агент.</summary>
public interface IRevitProcess
{
    int Pid { get; }
    bool HasExited { get; }
    long WorkingSetMb { get; }

    /// <summary>Завершается, когда процесс закрылся — сам или убитый.</summary>
    Task Exited { get; }

    void Kill();
}

public interface IProcessLauncher
{
    IRevitProcess Start(int year, string exe, IReadOnlyDictionary<string, string> environment);
}

/// <summary>Запуск настоящего Revit.exe с переменными окружения для аддина; аддин заранее помечен доверенным.</summary>
public sealed class ProcessLauncher(AddinTrust? trust = null) : IProcessLauncher
{
    public IRevitProcess Start(int year, string exe, IReadOnlyDictionary<string, string> environment)
    {
        trust?.Ensure(year);
        var info = new ProcessStartInfo(exe, "/nosplash") { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(exe)! };
        foreach (var (name, value) in environment) info.Environment[name] = value;
        var process = Process.Start(info) ?? throw new InvalidOperationException($"Не удалось запустить {exe}");
        return new RevitProcess(process);
    }

    private sealed class RevitProcess : IRevitProcess
    {
        private readonly Process _process;

        public RevitProcess(Process process)
        {
            _process = process;
            _process.EnableRaisingEvents = true;
            Exited = _process.WaitForExitAsync();
        }

        public int Pid => _process.Id;
        public bool HasExited => _process.HasExited;
        public Task Exited { get; }

        public long WorkingSetMb
        {
            get
            {
                try
                {
                    _process.Refresh();
                    return _process.WorkingSet64 / (1024 * 1024);
                }
                catch (InvalidOperationException)
                {
                    return 0;
                }
            }
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
