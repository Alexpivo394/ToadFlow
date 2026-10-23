using ToadFlow.Server;
using ToadFlow.Server.Export;
using ToadFlow.Server.Modules;

var builder = WebApplication.CreateBuilder(args);
builder.AddToadFlowServer();
builder.AddServerModule<ExportServerModule>();

var app = builder.Build();
app.UseToadFlowServer();
await app.RunAsync();

public partial class Program;
