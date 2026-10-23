using Build.Options;
using Microsoft.Extensions.Options;
using ModularPipelines.Attributes;
using ModularPipelines.Conditions;
using ModularPipelines.Context;
using ModularPipelines.Git.Extensions;
using ModularPipelines.Modules;

namespace Build.Modules;

/// <summary>
///     Clean projects and artifact directories.
/// </summary>
[SkipIf<IsCI>]
public sealed class CleanProjectModule(IOptions<BuildOptions> buildOptions) : SyncModule
{
    protected override void ExecuteModule(IModuleContext context, CancellationToken cancellationToken)
    {
        var rootDirectory = context.Git().RootDirectory;
        var outputDirectory = rootDirectory.GetFolder(buildOptions.Value.OutputDirectory);
        // Only .NET project folders: web/node_modules also contains "bin" folders.
        var buildOutputDirectories = new[] { "src", "tests", "install" }
            .Select(rootDirectory.GetFolder)
            .Where(folder => folder.Exists)
            .SelectMany(folder => folder.GetFolders(child => child.Name is "bin" or "obj" && child.Parent!.ListFiles().Any(file => file.Extension == ".csproj")));

        foreach (var buildFolder in buildOutputDirectories)
        {
            buildFolder.Clean();
        }

        if (outputDirectory.Exists)
        {
            outputDirectory.Clean();
        }
    }
}