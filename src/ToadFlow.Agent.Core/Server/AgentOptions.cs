using System.Reflection;
using Microsoft.AspNetCore.Http.Connections;
using ToadFlow.Agent.Instances;
using ToadFlow.Agent.Revit;

namespace ToadFlow.Agent.Server;

/// <summary>Настройки ядра агента. Значения по умолчанию — для настоящей работы; тесты подменяют сеть, Revit и интервалы.</summary>
public sealed class AgentOptions
{
    public string MachineName { get; init; } = Environment.MachineName;

    public string AgentVersion { get; init; } =
        (Assembly.GetEntryAssembly() ?? typeof(AgentOptions).Assembly).GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? "0.0.0";

    /// <summary>Как часто сообщать серверу, что агент жив, и состояние экземпляров.</summary>
    public TimeSpan HeartbeatInterval { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>Как часто спрашивать задания для свободных экземпляров.</summary>
    public TimeSpan PollInterval { get; init; } = TimeSpan.FromSeconds(15);

    /// <summary>Только для тестов и диагностики: молчание Revit вместо минут из настроек сервера.</summary>
    public TimeSpan? SilenceTimeoutOverride { get; init; }

    /// <summary>null — искать установленные Revit на машине.</summary>
    public IReadOnlyList<RevitInstallation>? Revits { get; init; }

    /// <summary>null — запускать настоящий Revit.exe.</summary>
    public IProcessLauncher? Launcher { get; init; }

    /// <summary>null — физическая память этой машины.</summary>
    public ISystemMemory? Memory { get; init; }

    /// <summary>Для тестов: обработчик сервера в памяти.</summary>
    public Func<HttpMessageHandler>? HttpMessageHandlerFactory { get; init; }

    /// <summary>null — WebSocket с запасными вариантами, как решит клиент SignalR.</summary>
    public HttpTransportType? Transport { get; init; }
}
