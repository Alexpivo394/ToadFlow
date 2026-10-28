using ToadFlow.Server;
using ToadFlow.Server.Clash;
using ToadFlow.Server.Export;
using ToadFlow.Server.Loi;
using ToadFlow.Server.Modules;

var builder = WebApplication.CreateBuilder(args);
builder.AddToadFlowServer();
builder.AddServerModule<ExportServerModule>();
builder.AddServerModule<LoiServerModule>();
builder.AddServerModule<ClashServerModule>();

var app = builder.Build();
app.UseToadFlowServer();
await app.RunAsync();

public partial class Program;
