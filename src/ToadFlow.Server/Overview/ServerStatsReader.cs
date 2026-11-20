using System.Reflection;
using Microsoft.EntityFrameworkCore;
using ToadFlow.Server.Data;
using ToadFlow.Server.Modules;

namespace ToadFlow.Server.Overview;

/// <summary>Когда запустился сервер — для «работает N дней» и молчания рабочих процессов без единого обращения.</summary>
public sealed class ServerStart(TimeProvider time)
{
    public DateTimeOffset At { get; } = time.GetUtcNow();
}

/// <summary>Место на томе, где лежит папка: на Linux — statvfs самого пути (том в контейнере), на Windows — корень диска.</summary>
public sealed class PathVolume(string name, Func<string> path) : IDataVolume
{
    public string Name => name;

    public VolumeStat? Read()
    {
        // Папки ещё нет (ничего не записано) — место того тома, где она появится.
        var root = Path.GetFullPath(path());
        while (!Directory.Exists(root) && Path.GetDirectoryName(root) is { } parent) root = parent;
        if (!Directory.Exists(root)) return null;
        var drive = new DriveInfo(OperatingSystem.IsWindows() ? Path.GetPathRoot(root)! : root);
        return new VolumeStat(name, drive.AvailableFreeSpace, drive.TotalSize);
    }
}

/// <summary>Сведения о сервере для «Обзора» и страницы «Сервер»: версия, время работы, база, тома, память, рабочие процессы.</summary>
public sealed class ServerStatsReader(ToadFlowDb db, ServerStart start, ModuleSet modules, IEnumerable<IDataVolume> volumes,
    IEnumerable<IWorkerProbe> workers, TimeProvider time, ILogger<ServerStatsReader> logger)
{
    /// <summary>Рабочий процесс молчит дольше — «не на связи».</summary>
    public static readonly TimeSpan WorkerSilence = TimeSpan.FromMinutes(5);

    public async Task<ServerStats> ReadAsync(CancellationToken ct)
    {
        var now = time.GetUtcNow();
        return new ServerStats(Version, start.At, await DatabaseBytesAsync(ct), Volumes(), Environment.WorkingSet,
            Workers().Select(w => new WorkerStat(w.Module, w.Title, w.LastSeenAt, SilentFor(w, now) <= WorkerSilence)).ToList());
    }

    /// <summary>Рабочие процессы включённых модулей.</summary>
    public IEnumerable<IWorkerProbe> Workers() => workers.Where(w => modules.IsEnabled(w.Module));

    public static TimeSpan SilentFor(IWorkerProbe worker, DateTimeOffset now) => now - (worker.LastSeenAt ?? worker.StartedAt);

    public IReadOnlyList<VolumeStat> Volumes()
    {
        var stats = new List<VolumeStat>();
        foreach (var volume in volumes)
        {
            try
            {
                if (volume.Read() is { TotalBytes: > 0 } stat) stats.Add(stat);
            }
            catch (Exception exception)
            {
                logger.LogWarning(exception, "Не удалось узнать место на томе {Volume}", volume.Name);
            }
        }

        return stats;
    }

    private static string Version
    {
        get
        {
            var assembly = Assembly.GetEntryAssembly() ?? typeof(ServerStatsReader).Assembly;
            var version = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
                          ?? assembly.GetName().Version?.ToString() ?? "?";
            return version.Split('+')[0]; // без хеша коммита
        }
    }

    private async Task<long> DatabaseBytesAsync(CancellationToken ct)
    {
        try
        {
            return await db.Database.SqlQuery<long>($"SELECT pg_database_size(current_database()) AS \"Value\"").SingleAsync(ct);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(exception, "Не удалось узнать размер базы");
            return 0;
        }
    }
}
