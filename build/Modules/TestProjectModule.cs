using ModularPipelines.Attributes;
using ModularPipelines.Context;
using ModularPipelines.DotNet.Extensions;
using ModularPipelines.DotNet.Options;
using ModularPipelines.Modules;
using Sourcy.DotNet;

namespace Build.Modules;

/// <summary>
///     Run the agent and add-in tests. They need neither Revit nor a database server, so they also run in CI.
/// </summary>
[DependsOn<CompileProjectModule>(Optional = true)]
public sealed class TestProjectModule : Module
{
    protected override async Task ExecuteModuleAsync(IModuleContext context, CancellationToken cancellationToken)
    {
        string[] projects = [Projects.ToadFlow_Agent_Tests.FullName, Projects.ToadFlow_Revit_Addin_Tests.FullName];
        foreach (var project in projects)
        {
            await context.SubModule(Path.GetFileNameWithoutExtension(project), async () => await context.DotNet().Test(new DotNetTestOptions
            {
                Project = project,
                Configuration = "Release"
            }, cancellationToken: cancellationToken));
        }
    }
}
