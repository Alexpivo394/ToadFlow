using ToadFlow.Server;
using ToadFlow.Server.Clash;
using ToadFlow.Server.Export;
using ToadFlow.Server.Health;
using ToadFlow.Server.Loi;
using ToadFlow.Server.Modules;

// Healthcheck контейнера: запрос к /api/health работающего сервера, без запуска второго.
if (args.Contains(HealthCheck.Argument)) return await HealthCheck.RunAsync();

var builder = WebApplication.CreateBuilder(args);
builder.AddToadFlowServer();
builder.AddServerModule<ExportServerModule>();
builder.AddServerModule<LoiServerModule>();
builder.AddServerModule<ClashServerModule>();

var app = builder.Build();
app.UseToadFlowServer();
await app.RunAsync();
return 0;

public partial class Program;
