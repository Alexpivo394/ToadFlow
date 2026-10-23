using System.Text;
using Microsoft.Extensions.Logging;

namespace ToadFlow.Agent.App;

/// <summary>Логи агента: файл на каждый день (agent-2026-10-23.log), старше 14 дней удаляются.</summary>
internal sealed class FileLoggerProvider : ILoggerProvider
{
    private const int KeepDays = 14;

    private readonly string _folder;
    private readonly object _sync = new();
    private StreamWriter? _writer;
    private DateOnly _day;

    public FileLoggerProvider(string folder)
    {
        _folder = folder;
        Directory.CreateDirectory(folder);
        foreach (var old in Directory.GetFiles(folder, "agent-*.log").Where(f => File.GetLastWriteTime(f) < DateTime.Now.AddDays(-KeepDays)))
        {
            try
            {
                File.Delete(old);
            }
            catch (IOException)
            {
                // занят — удалим в следующий раз
            }
        }
    }

    public ILogger CreateLogger(string categoryName) => new FileLogger(this, categoryName.Split('.').Last());

    public void Dispose()
    {
        lock (_sync) _writer?.Dispose();
    }

    private void Write(string line)
    {
        lock (_sync)
        {
            var today = DateOnly.FromDateTime(DateTime.Now);
            if (_writer is null || _day != today)
            {
                _writer?.Dispose();
                _day = today;
                _writer = new StreamWriter(new FileStream(Path.Combine(_folder, $"agent-{today:yyyy-MM-dd}.log"), FileMode.Append, FileAccess.Write, FileShare.Read),
                    new UTF8Encoding(true)) { AutoFlush = true };
            }

            _writer.WriteLine(line);
        }
    }

    private sealed class FileLogger(FileLoggerProvider provider, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Information;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel)) return;
            var line = $"{DateTime.Now:HH:mm:ss} {Level(logLevel)} {category}: {formatter(state, exception)}";
            if (exception is not null) line += Environment.NewLine + exception;
            provider.Write(line);
        }

        private static string Level(LogLevel level) => level switch
        {
            LogLevel.Warning => "WARN",
            LogLevel.Error or LogLevel.Critical => "ERROR",
            _ => "INFO"
        };
    }
}
