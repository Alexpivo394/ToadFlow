using Microsoft.Extensions.Logging;
using Shouldly;
using ToadFlow.Agent.Logging;

namespace ToadFlow.Agent.Tests.Logging;

public class FileLoggerTests
{
    /// <summary>
    ///     Сегодняшний файл держит другой процесс (как файл другого пользователя или полный диск — запись не проходит), а папки нет
    ///     совсем: ни конструктор, ни запись не бросают.
    /// </summary>
    [Test]
    public void Logger_DiskFullOrForeignFile_NeverThrows()
    {
        var folder = Path.Combine(Path.GetTempPath(), "toadflow-logs-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        var today = Path.Combine(folder, $"agent-{DateTime.Now:yyyy-MM-dd}.log");
        try
        {
            using (new FileStream(today, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                using var provider = new FileLoggerProvider(folder);
                var logger = provider.CreateLogger("ToadFlow.Agent.Test");
                Should.NotThrow(() => logger.LogInformation("строка"));
                Should.NotThrow(() => logger.LogError(new IOException("сбой"), "ещё строка"));
            }

            // Папку не создать (путь занят файлом).
            using var broken = Should.NotThrow(() => new FileLoggerProvider(today));
            Should.NotThrow(() => broken.CreateLogger("x").LogWarning("в никуда"));
        }
        finally
        {
            Directory.Delete(folder, true);
        }
    }
}
