using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using MysticMind.PostgresEmbed;
using Npgsql;
using ToadFlow.Server.Data;
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

    /// <summary>
    ///     Создаёт базу с уникальным именем — копию шаблона с накатанными миграциями — и возвращает строку подключения к ней. Сервер теста
    ///     при старте видит, что миграций накатывать не нужно: на тест уходят миллисекунды вместо секунд.
    /// </summary>
    public async Task<string> CreateDatabaseAsync()
    {
        await EnsureTemplateAsync();
        var name = "t_" + Guid.NewGuid().ToString("N");
        await ExecuteAsync($"create database {name} template {Template}");
        return ConnectionString(name);
    }

    private const string Template = "toadflow_template";
    private readonly SemaphoreSlim _templateLock = new(1, 1);
    private bool _templateReady;

    /// <summary>
    ///     Шаблон — пустая база, на которую сервер накатил миграции, и только: остальные фоновые службы (заводящие администратора и т. п.)
    ///     не запускаются, чтобы их данные с настоящим временем не попали в тесты с подменным. Подключения к шаблону без пула: копировать
    ///     базу, к которой кто-то подключён, Postgres не даёт.
    /// </summary>
    private async Task EnsureTemplateAsync()
    {
        if (Volatile.Read(ref _templateReady)) return;
        await _templateLock.WaitAsync();
        try
        {
            if (_templateReady) return;
            await ExecuteAsync($"drop database if exists {Template} with (force)");
            await ExecuteAsync($"create database {Template}");
            await using (var server = new ServerFactory(ConnectionString(Template) + ";Pooling=false", services: OnlyMigrations)) _ = server.Server;
            Volatile.Write(ref _templateReady, true);
        }
        finally
        {
            _templateLock.Release();
        }
    }

    private static void OnlyMigrations(IServiceCollection services)
    {
        foreach (var hosted in services.Where(s => s.ServiceType == typeof(IHostedService) && s.ImplementationType != typeof(MigrationRunner)).ToList())
            services.Remove(hosted);
    }

    private async Task ExecuteAsync(string sql)
    {
        await using var connection = new NpgsqlConnection(ConnectionString("postgres"));
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }

    /// <summary>Пустая база без миграций — для проверки обновления схемы со старой версии.</summary>
    public async Task<string> CreateEmptyDatabaseAsync()
    {
        var name = "e_" + Guid.NewGuid().ToString("N");
        await ExecuteAsync($"create database {name}");
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

    private readonly System.Collections.Concurrent.ConcurrentBag<Task> _drops = [];
    private readonly SemaphoreSlim _dropSlots = new(2, 2);

    /// <summary>
    ///     Удаляет базу теста в фоне: удаление (около 0.7 с под нагрузкой) не держит слот следующего теста. Одновременно — не больше двух
    ///     удалений; конец сессии дожидается всех.
    /// </summary>
    public void DropDatabaseLater(string connectionString) => _drops.Add(Task.Run(async () =>
    {
        await _dropSlots.WaitAsync();
        try
        {
            await DropDatabaseAsync(connectionString);
        }
        finally
        {
            _dropSlots.Release();
        }
    }));

    public async ValueTask DisposeAsync()
    {
        try
        {
            await Task.WhenAll(_drops);
        }
        catch (Exception exception) when (exception is NpgsqlException or PostgresException)
        {
            // Postgres всё равно останавливается, а его папка удаляется при следующем запуске.
        }

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
