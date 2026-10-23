#if DEBUG
using MysticMind.PostgresEmbed;

namespace ToadFlow.Server.Dev;

/// <summary>
///     Postgres без Docker для локальной разработки: при <c>Database:Embedded=true</c> поднимается встроенный сервер
///     с данными в %LocalAppData%\ToadFlow\pgdata, а строка подключения подменяется на него.
/// </summary>
internal static class EmbeddedPostgres
{
    private const int Port = 54329;
    private const string Password = "toadflow_dev";

    public static void UseIfConfigured(WebApplicationBuilder builder)
    {
        if (!builder.Configuration.GetValue<bool>("Database:Embedded")) return;

        var dataRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ToadFlow", "pgdata");
        Directory.CreateDirectory(dataRoot);

        // Сервер стартует до построения хоста: миграции при запуске уже должны куда-то подключаться.
        var server = new PgServer("17.11.0", dbDir: dataRoot, port: Port, instanceId: Guid.Parse("7f1c2b5e-0d4a-4f3e-9a51-6b2d8c0e4a11"),
            clearInstanceDirOnStop: false,
            // Журнал — в файлы: PostgresEmbed не читает вывод сервера, и при полном буфере Postgres зависает на записи в stderr.
            pgServerParams: new Dictionary<string, string> { ["logging_collector"] = "on", ["log_directory"] = "pglog" });
        server.Start();
        AppDomain.CurrentDomain.ProcessExit += (_, _) => server.Stop();

        builder.Configuration["ConnectionStrings:Postgres"] =
            $"Host=localhost;Port={Port};Database=toadflow;Username=postgres;Password={Password}";
        EnsureDatabase();
    }

    private static void EnsureDatabase()
    {
        using var connection = new Npgsql.NpgsqlConnection($"Host=localhost;Port={Port};Database=postgres;Username=postgres;Password={Password}");
        OpenWhenReady(connection);
        using var exists = new Npgsql.NpgsqlCommand("select 1 from pg_database where datname = 'toadflow'", connection);
        if (exists.ExecuteScalar() is not null) return;

        using var create = new Npgsql.NpgsqlCommand("create database toadflow", connection);
        create.ExecuteNonQuery();
    }

    /// <summary>
    ///     После аварийной остановки (например, процесс убили) Postgres при старте восстанавливается и какое-то время
    ///     отвечает «система БД запускается» (57P03) — ждём до минуты.
    /// </summary>
    private static void OpenWhenReady(Npgsql.NpgsqlConnection connection)
    {
        var deadline = DateTime.UtcNow.AddMinutes(1);
        while (true)
        {
            try
            {
                connection.Open();
                return;
            }
            catch (Npgsql.PostgresException exception) when (exception.SqlState == "57P03" && DateTime.UtcNow < deadline)
            {
                Thread.Sleep(500);
            }
        }
    }
}
#endif
