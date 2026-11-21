using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using MysticMind.PostgresEmbed;
using Npgsql;
using ToadFlow.Server.Data;
using TUnit.Core.Interfaces;

namespace ToadFlow.Server.Tests.Infrastructure;

/// <summary>
///     Один встроенный Postgres на тестовую сессию; каждому тесту — своя чистая база. Базы не удаляются после теста, а очищаются
///     и выдаются следующим: создание и удаление базы в Postgres идут почти по одному и не дают тестам работать параллельно.
/// </summary>
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
        await WaitReadyAsync();
    }

    /// <summary>
    ///     PostgresEmbed возвращается, когда процесс запущен, а не когда база принимает подключения: первый тест получал
    ///     57P03 «система баз данных запускается».
    /// </summary>
    private async Task WaitReadyAsync()
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                await ScalarAsync(ConnectionString("postgres") + ";Pooling=false", "select 1");
                return;
            }
            catch (PostgresException exception) when (exception.SqlState == PostgresErrorCodes.CannotConnectNow && attempt < 300)
            {
                await Task.Delay(100);
            }
        }
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
            _reusable = await ScalarAsync(ConnectionString(Template) + ";Pooling=false", NonEmptyTables) is null or "";
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

    private readonly System.Collections.Concurrent.ConcurrentBag<string> _clean = [];
    private bool _reusable;

    /// <summary>Таблицы с данными, через запятую; журнал миграций не считается.</summary>
    private const string NonEmptyTables = """
        create or replace function pg_temp.non_empty_tables() returns text language plpgsql as $$
        declare
            names text := '';
            item record;
            filled boolean;
        begin
            for item in select format('%I.%I', schemaname, tablename) as name from pg_tables
                        where schemaname not in ('pg_catalog', 'information_schema') and tablename <> '__EFMigrationsHistory' loop
                execute 'select exists(select 1 from ' || item.name || ')' into filled;
                if filled then names := names || case when names = '' then '' else ', ' end || item.name; end if;
            end loop;
            return names;
        end $$;
        select pg_temp.non_empty_tables();
        """;

    /// <summary>
    ///     Возвращает базу в состояние шаблона: закрывает оставшиеся подключения, очищает таблицы с данными и сбрасывает счётчики.
    ///     Трогаются только непустые таблицы — очистка всех подряд на каждый тест заметно дороже.
    /// </summary>
    private const string Clean = """
        select pg_terminate_backend(pid) from pg_stat_activity where datname = current_database() and pid <> pg_backend_pid();
        set lock_timeout = '10s';
        do $$
        declare
            names text;
            item record;
        begin
            names := pg_temp.non_empty_tables();
            if names <> '' then execute 'truncate table ' || names || ' restart identity cascade'; end if;
            for item in select format('%I.%I', schemaname, sequencename) as name from pg_sequences where last_value is not null loop
                execute 'alter sequence ' || item.name || ' restart';
            end loop;
        end $$;
        """;

    /// <summary>Чистая база для теста: очищенная после прошлого теста или, если таких нет, новая копия шаблона.</summary>
    public async Task<string> RentDatabaseAsync()
    {
        await EnsureTemplateAsync();
        return _clean.TryTake(out var connectionString) ? connectionString : await CreateDatabaseAsync();
    }

    /// <summary>
    ///     Принимает базу после теста: в фоне очищает её и отдаёт следующим тестам. Если очистить не вышло или в шаблоне есть
    ///     данные (их очистка стёрла бы), база удаляется, как раньше.
    /// </summary>
    public void ReturnDatabaseLater(string connectionString) => _drops.Add(Task.Run(async () =>
    {
        if (_reusable)
        {
            try
            {
                var command = NonEmptyTables.Replace("select pg_temp.non_empty_tables();", "") + Clean;
                await ScalarAsync(connectionString + ";Pooling=false", command);
                _clean.Add(connectionString);
                return;
            }
            catch (Exception exception) when (exception is NpgsqlException or TimeoutException or IOException)
            {
                // Не очистилась — удаляем, тест получит новую.
            }
        }

        await DropDatabaseAsync(connectionString);
    }));

    private static async Task<object?> ScalarAsync(string connectionString, string sql)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        return await command.ExecuteScalarAsync();
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

    /// <summary>
    ///     Закрывает соединения остановленного сервера с его базой. Пул соединений живёт в процессе тестов, а не в сервере: без этого
    ///     соединения каждого теста висели бы до удаления его базы, и Postgres упирался бы в лимит подключений.
    /// </summary>
    public static void ClosePool(string connectionString)
    {
        using var connection = new NpgsqlConnection(connectionString);
        NpgsqlConnection.ClearPool(connection);
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
    ///     Данные прошлых прогонов удаляются при старте, а не при остановке: сразу после остановки Postgres ещё держит свои файлы.
    ///     PostgresEmbed кладёт каждый запуск в <c>pg_embed/{guid}</c> — около 170 МБ; скачанный архив (<c>pg_embed/binaries</c>) остаётся,
    ///     чтобы не качать его каждый раз. Папку работающего Postgres (идёт другой прогон тестов) трогать нельзя: он открывает файлы
    ///     с разрешением на удаление, и удаление испортило бы чужой прогон.
    /// </summary>
    private static void RemoveLeftovers()
    {
        var instances = Path.Combine(Root, "pg_embed");
        if (Directory.Exists(instances))
            foreach (var dir in Directory.GetDirectories(instances).Where(dir => Guid.TryParse(Path.GetFileName(dir), out _) && !IsRunning(dir)))
                TryDelete(dir);

        // Хранилища слепков серверов тестов, не убранные упавшим прогоном; свежие могут принадлежать идущему прогону.
        var clash = Path.Combine(Path.GetTempPath(), "toadflow-clash-tests");
        if (Directory.Exists(clash))
            foreach (var dir in Directory.GetDirectories(clash).Where(dir => Directory.GetLastWriteTimeUtc(dir) < DateTime.UtcNow.AddDays(-1)))
                TryDelete(dir);
    }

    /// <summary>Работает ли Postgres этой папки: при работе он держит <c>postmaster.pid</c> с номером своего процесса в первой строке.</summary>
    private static bool IsRunning(string instance)
    {
        try
        {
            var pidFile = Path.Combine(instance, "data", "postmaster.pid");
            if (!File.Exists(pidFile)) return false;
            using var reader = new StreamReader(new FileStream(pidFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete));
            if (!int.TryParse(reader.ReadLine(), out var pid)) return true;
            using var process = System.Diagnostics.Process.GetProcessById(Math.Abs(pid));
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            return false; // процесса с таким номером нет: Postgres был убит и файл остался
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            return true; // не удалось понять — не трогаем
        }
    }

    private static void TryDelete(string dir)
    {
        try
        {
            Directory.Delete(dir, true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Занята — уберёт следующий прогон.
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
