using System.Runtime.InteropServices;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ToadFlow.Agent.Config;

namespace ToadFlow.Agent.App;

internal static partial class Program
{
    private const string MutexName = @"Global\ToadFlowAgent";

    /// <summary>Синхронный и STA: WinForms (трей, окно регистрации) требует однопоточного апартамента.</summary>
    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Contains("--register", StringComparer.OrdinalIgnoreCase))
        {
            AttachConsole(-1); // вывод в консоль, из которой запустили (установщик, скрипт)
            return AgentCommandLine.RunAsync(args, Console.Out).GetAwaiter().GetResult() ?? 2;
        }

        using var mutex = new Mutex(true, MutexName, out var first);
        if (!first)
        {
            // автозапуск при входе, а агент уже работает — молча выходим
            if (args.Contains("--autostart", StringComparer.OrdinalIgnoreCase)) return 0;
            MessageBox.Show("Агент ToadFlow уже запущен — его значок в области уведомлений.", "ToadFlow", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return 0;
        }

        ApplicationConfiguration.Initialize();

        var config = AgentConfigStore.Load();
        if (config is null)
        {
            using var form = new RegistrationForm();
            if (form.ShowDialog() != DialogResult.OK || form.Config is null) return 1;
            config = form.Config;
        }

        var builder = Host.CreateApplicationBuilder();
        builder.Logging.ClearProviders();
        builder.Logging.AddProvider(new FileLoggerProvider(AgentPaths.Logs));
        builder.Logging.SetMinimumLevel(LogLevel.Information);
        builder.Services.AddToadFlowAgentCore(config);

        using var host = builder.Build();
        host.StartAsync().GetAwaiter().GetResult();
        host.Services.GetRequiredService<ILogger<TrayApplication>>().LogInformation("Агент запущен, сервер {Server}", config.ServerUrl);

        Application.Run(new TrayApplication(host.Services, config));

        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        host.StopAsync(stop.Token).GetAwaiter().GetResult();
        return 0;
    }

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool AttachConsole(int processId);
}
