using ToadFlow.Agent.Config;

namespace ToadFlow.Agent.App;

/// <summary>Первый запуск: адрес сервера и одноразовый код из «Настройки → Агенты».</summary>
internal sealed class RegistrationForm : Form
{
    private readonly TextBox _server = new() { Width = 340, PlaceholderText = "http://toadflow:8080" };
    private readonly TextBox _code = new() { Width = 340, PlaceholderText = "TF-XXXX-XXXX", CharacterCasing = CharacterCasing.Upper };
    private readonly Button _connect = new() { Text = "Подключить", AutoSize = true };
    private readonly CheckBox _trustAddin = new()
    {
        Text = "Загружать аддин ToadFlow в Revit без вопроса «Неподписанная надстройка»\n(на машине с агентом это окно некому нажать)",
        AutoSize = true,
        Checked = false,
        Margin = new Padding(0, 12, 0, 0)
    };

    private readonly Label _status = new() { AutoSize = true, MaximumSize = new Size(340, 0), ForeColor = Color.Firebrick };

    public RegistrationForm()
    {
        Text = "Подключение к ToadFlow";
        Icon = AppIcon.Get(SystemInformation.IconSize);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        Padding = new Padding(16);

        var layout = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, AutoSize = true, WrapContents = false };
        layout.Controls.Add(new Label { Text = "Адрес сервера ToadFlow", AutoSize = true });
        layout.Controls.Add(_server);
        layout.Controls.Add(new Label { Text = "Код подключения (Настройки → Агенты → «Подключить агента»)", AutoSize = true, Margin = new Padding(0, 12, 0, 0) });
        layout.Controls.Add(_code);
        layout.Controls.Add(_trustAddin);
        layout.Controls.Add(_status);
        layout.Controls.Add(_connect);
        Controls.Add(layout);

        AcceptButton = _connect;
        _connect.Click += async (_, _) => await ConnectAsync();
    }

    public AgentConfig? Config { get; private set; }

    private async Task ConnectAsync()
    {
        _connect.Enabled = false;
        _status.Text = "Подключаемся…";
        _status.ForeColor = SystemColors.GrayText;
        try
        {
            Config = await new Registration().RegisterAsync(_server.Text, _code.Text, Environment.MachineName) with { TrustAddin = _trustAddin.Checked };
            AgentConfigStore.Save(Config);
            DialogResult = DialogResult.OK;
            Close();
        }
        catch (AgentRegistrationException exception)
        {
            _status.ForeColor = Color.Firebrick;
            _status.Text = exception.Message;
        }
        finally
        {
            _connect.Enabled = true;
        }
    }
}
