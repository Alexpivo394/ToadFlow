using System.Diagnostics;
using System.Text.Json;

namespace ToadFlow.Agent.Instances;

/// <summary>Процессы машины — для поиска Revit, оставшихся от прошлого запуска агента.</summary>
public interface IProcessCatalog
{
    /// <summary>Жив ли процесс с этим PID и тем же временем запуска (PID после перезагрузки или выхода процесса занимают другие).</summary>
    bool IsAlive(int pid, DateTime startedAt);

    void Kill(int pid);
}

public sealed class SystemProcessCatalog : IProcessCatalog
{
    public bool IsAlive(int pid, DateTime startedAt)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            return !process.HasExited && OrphanRegistry.StartTimeOf(process) is { } started && Math.Abs((started - startedAt).TotalSeconds) < 1;
        }
        catch (ArgumentException)
        {
            return false; // такого процесса нет
        }
    }

    public void Kill(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            process.Kill(entireProcessTree: true);
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            // уже закрылся
        }
    }
}

/// <summary>
///     Список запущенных агентом Revit (PID и время запуска) в файле. Revit в объекте задания (<see cref="JobObject" />) и так закрывается
///     вместе с агентом; список — запасной путь: если объект задания не сработал, при следующем старте агент закрывает оставшиеся
///     Revit сам. Чужие процессы не трогаются: совпасть должны и PID, и время запуска.
/// </summary>
public sealed class OrphanRegistry(string path, IProcessCatalog catalog)
{
    private readonly object _sync = new();

    /// <summary><c>%LocalAppData%\ToadFlow\revit-instances.json</c>.</summary>
    public static string DefaultPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ToadFlow", "revit-instances.json");

    public void Add(int pid, DateTime startedAt) => Update(list => list.Add(new Entry(pid, startedAt)));

    public void Remove(int pid) => Update(list => list.RemoveAll(e => e.Pid == pid));

    /// <summary>При старте агента: закрыть Revit, оставшиеся от прошлого запуска; список очищается.</summary>
    /// <returns>Сколько процессов закрыто.</returns>
    public int KillLeftovers()
    {
        var killed = 0;
        Update(list =>
        {
            foreach (var entry in list.Where(e => catalog.IsAlive(e.Pid, e.StartedAt)))
            {
                catalog.Kill(entry.Pid);
                killed++;
            }

            list.Clear();
        });
        return killed;
    }

    public static DateTime? StartTimeOf(Process process)
    {
        try
        {
            return process.StartTime.ToUniversalTime();
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
        {
            return null;
        }
    }

    private void Update(Action<List<Entry>> change)
    {
        lock (_sync)
        {
            List<Entry> list;
            try
            {
                list = File.Exists(path) ? JsonSerializer.Deserialize<List<Entry>>(File.ReadAllText(path)) ?? [] : [];
            }
            catch (Exception exception) when (exception is IOException or JsonException or UnauthorizedAccessException)
            {
                list = [];
            }

            change(list);
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllText(path, JsonSerializer.Serialize(list));
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // список — запасной путь; без него Revit всё равно в объекте задания
            }
        }
    }

    private sealed record Entry(int Pid, DateTime StartedAt);
}
