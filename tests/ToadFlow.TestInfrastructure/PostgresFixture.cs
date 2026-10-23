using System.Net;
using System.Net.Sockets;
using MysticMind.PostgresEmbed;
using Npgsql;
using TUnit.Core.Interfaces;

namespace ToadFlow.Server.Tests.Infrastructure;

/// <summary>Один встроенный Postgres на тестовую сессию; каждому тесту — своя чистая база.</summary>
public sealed class PostgresFixture : IAsyncInitializer, IAsyncDisposable
{
    private PgServer? _server;
    private int _port;

    private static readonly string Root = Path.Combine(Path.GetTempPath(), "toadflow-tests-pg");

    /// <summary>
    ///     Журнал — в файлы: PostgresEmbed перенаправляет вывод сервера и не читает его, и когда буфер заполняется
    ///     (миграции EF пишут NOTICE), процессы Postgres блокируются на записи в stderr — запросы висят без ожиданий.
    ///     Долговечность тестовым базам не нужна.
    /// </summary>
    private static readonly Dictionary<string, string> Parameters = new()
    {
        ["logging_collector"] = "on",
        ["log_directory"] = "pglog",
        ["fsync"] = "off",
        ["synchronous_commit"] = "off",
        ["full_page_writes"] = "off",
        ["max_connections"] = "300"
    };

    public async Task InitializeAsync()
    {
        RemoveLeftovers();
        _port = FreePort();
        _server = new PgServer("17.11.0", dbDir: Root, port: _port, pgServerParams: Parameters, clearInstanceDirOnStop: false);
        await _server.StartAsync();
    }

    /// <summary>Создаёт пустую базу с уникальным именем и возвращает строку подключения к ней.</summary>
    public async Task<string> CreateDatabaseAsync()
    {
        var name = "t_" + Guid.NewGuid().ToString("N");
        await using (var connection = new NpgsqlConnection(ConnectionString("postgres")))
        {
            await connection.OpenAsync();
            await using var command = new NpgsqlCommand($"create database {name}", connection);
            await command.ExecuteNonQueryAsync();
        }

        return ConnectionString(name);
    }

    /// <summary>
    ///     Удаляет базу теста. <c>WITH (FORCE)</c> закрывает её подключения — иначе пулы соединений уже остановленных
    ///     серверов копятся и Postgres упирается в лимит подключений.
    /// </summary>
    public async Task DropDatabaseAsync(string connectionString)
    {
        var name = new NpgsqlConnectionStringBuilder(connectionString).Database;
        await using var connection = new NpgsqlConnection(ConnectionString("postgres"));
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand($"drop database if exists {name} with (force)", connection);
        await command.ExecuteNonQueryAsync();
    }

    public async ValueTask DisposeAsync()
    {
        if (_server is not null) await _server.StopAsync();
    }

    /// <summary>
    ///     Данные прошлых прогонов удаляются при старте, а не при остановке: сразу после остановки Postgres ещё держит
    ///     свои файлы. Скачанные бинарники (папка без GUID в имени) остаются, чтобы не качать их каждый раз.
    /// </summary>
    private static void RemoveLeftovers()
    {
        if (!Directory.Exists(Root)) return;

        foreach (var dir in Directory.GetDirectories(Root).Where(dir => Guid.TryParse(Path.GetFileName(dir), out _)))
        {
            try
            {
                Directory.Delete(dir, true);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // Занята другим прогоном тестов — оставляем.
            }
        }
    }

    private string ConnectionString(string database) =>
        $"Host=localhost;Port={_port};Database={database};Username=postgres;Password=postgres;Include Error Detail=true";

    private static int FreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }
}
