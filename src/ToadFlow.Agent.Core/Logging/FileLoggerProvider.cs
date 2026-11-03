using System.Text;
using Microsoft.Extensions.Logging;

namespace ToadFlow.Agent.Logging;

/// <summary>
///     Логи агента: файл на каждый день (agent-2026-10-23.log), старше 14 дней удаляются. Логгер никогда не бросает: заполненный диск
///     или сегодняшний файл другого пользователя машины (папка логов общая) не должны останавливать агента — строка теряется, а файл
///     пробуется снова через минуту.
/// </summary>
public sealed class FileLoggerProvider : ILoggerProvider
{
    private const int KeepDays = 14;
    private static readonly TimeSpan RetryAfter = TimeSpan.FromMinutes(1);

    private readonly string _folder;
    private readonly object _sync = new();
    private StreamWriter? _writer;
    private DateOnly _day;
    private DateTime _failedAt = DateTime.MinValue;

    public FileLoggerProvider(string folder)
    {
        _folder = folder;
        try
        {
            Directory.CreateDirectory(folder);
            foreach (var old in Directory.GetFiles(folder, "agent-*.log").Where(f => File.GetLastWriteTime(f) < DateTime.Now.AddDays(-KeepDays)))
            {
                try
                {
                    File.Delete(old);
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    // занят или чужой — удалим в следующий раз (или удалит его владелец)
                }
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // папки нет и не создать — писать будем, когда получится
        }
    }

    public ILogger CreateLogger(string categoryName) => new FileLogger(this, categoryName.Split('.').Last());

    public void Dispose()
    {
        lock (_sync) Close();
    }

    private void Write(string line)
    {
        lock (_sync)
        {
            try
            {
                var today = DateOnly.FromDateTime(DateTime.Now);
                if (_writer is null || _day != today)
                {
                    Close();
                    if (DateTime.UtcNow - _failedAt < RetryAfter) return;
                    _day = today;
                    var name = Path.Combine(_folder, $"agent-{today:yyyy-MM-dd}.log");
                    _writer = new StreamWriter(new FileStream(name, FileMode.Append, FileAccess.Write, FileShare.Read), new UTF8Encoding(true)) { AutoFlush = true };
                }

                _writer.WriteLine(line);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                _failedAt = DateTime.UtcNow;
                Close();
            }
        }
    }

    /// <summary>Под <see cref="_sync" />.</summary>
    private void Close()
    {
        try
        {
            _writer?.Dispose();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // не дописался буфер — диск полон
        }

        _writer = null;
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
