using ModularPipelines.Attributes;
using ModularPipelines.Context;
using ModularPipelines.DotNet.Extensions;
using ModularPipelines.DotNet.Options;
using ModularPipelines.FileSystem;
using ModularPipelines.Modules;
using Sourcy.DotNet;

namespace Build.Modules;

/// <summary>
///     Publish the agent as a self-contained win-x64 application: farm machines need no .NET runtime.
/// </summary>
[DependsOn<ResolveVersioningModule>]
[DependsOn<CleanProjectModule>(Optional = true)]
public sealed class PublishAgentModule : Module<string>
{
    protected override async Task<string?> ExecuteAsync(IModuleContext context, CancellationToken cancellationToken)
    {
        var versioning = (await context.GetModule<ResolveVersioningModule>()).ValueOrDefault!;
        var output = Folder.CreateTemporaryFolder();

        await context.DotNet().Publish(new DotNetPublishOptions
        {
            ProjectSolution = Projects.ToadFlow_Agent.FullName,
            Configuration = "Release",
            Runtime = "win-x64",
            Output = output.Path,
            Properties =
            [
                ("SelfContained", "true"),
                ("VersionPrefix", versioning.VersionPrefix),
                ("VersionSuffix", versioning.VersionSuffix!)
            ]
        }, cancellationToken: cancellationToken);

        return output.Path;
    }
}
