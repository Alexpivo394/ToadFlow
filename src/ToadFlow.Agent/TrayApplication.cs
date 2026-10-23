using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using ToadFlow.Agent.Config;
using ToadFlow.Agent.Instances;
using ToadFlow.Agent.Server;
using ToadFlow.Contracts;

namespace ToadFlow.Agent.App;

/// <summary>Значок агента в области уведомлений: состояние в подсказке, меню «Открыть ToadFlow», «Папка логов», «Выход».</summary>
internal sealed class TrayApplication : ApplicationContext
{
    private readonly NotifyIcon _icon;
    private readonly System.Windows.Forms.Timer _timer;
    private readonly ServerConnection _connection;
    private readonly InstanceManager _instances;

    public TrayApplication(IServiceProvider services, AgentConfig config)
    {
        _connection = services.GetRequiredService<ServerConnection>();
        _instances = services.GetRequiredService<InstanceManager>();

        var menu = new ContextMenuStrip();
        menu.Items.Add("Открыть ToadFlow", null, (_, _) => Open(config.ServerUrl));
        menu.Items.Add("Папка логов", null, (_, _) => Open(AgentPaths.Logs));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Выход", null, (_, _) => Exit());

        _icon = new NotifyIcon { Icon = SystemIcons.Application, ContextMenuStrip = menu, Visible = true, Text = "ToadFlow" };
        _icon.DoubleClick += (_, _) => Open(config.ServerUrl);

        _timer = new System.Windows.Forms.Timer { Interval = 3000 };
        _timer.Tick += (_, _) => Refresh();
        _timer.Start();
        Refresh();
    }

    /// <summary>«ToadFlow · в сети · Revit: 2 · выгружает АР.rvt» — подсказка ограничена 127 символами.</summary>
    private void Refresh()
    {
        var instances = _instances.Instances;
        var busy = instances.FirstOrDefault(i => i.State is InstanceState.Busy or InstanceState.Unresponsive && i.ModelName is not null);
        var parts = new List<string>
        {
            "ToadFlow",
            !_connection.IsConnected ? "нет связи с сервером" : _connection.Settings?.Draining == true ? "не берёт новые" : "в сети",
            $"Revit: {instances.Count}"
        };
        if (busy is not null) parts.Add($"выгружает {busy.ModelName}");
        var text = string.Join(" · ", parts);
        _icon.Text = text.Length > 127 ? text[..124] + "…" : text;
    }

    private void Exit()
    {
        var busy = _instances.Instances.Count(i => i.JobId is not null);
        if (busy > 0 && MessageBox.Show($"Идёт выгрузка ({busy}). Если выйти, Revit будет закрыт, а задания повторятся позже. Выйти?", "ToadFlow",
                MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes)
            return;

        _timer.Stop();
        _icon.Visible = false;
        ExitThread();
    }

    private static void Open(string target)
    {
        try
        {
            if (!target.StartsWith("http", StringComparison.OrdinalIgnoreCase)) Directory.CreateDirectory(target);
            Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
        }
        catch (Exception exception)
        {
            MessageBox.Show(exception.Message, "ToadFlow", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _timer.Dispose();
            _icon.Dispose();
        }

        base.Dispose(disposing);
    }
}
