using Microsoft.VisualStudio.SolutionPersistence.Serializer;
using ModularPipelines.Context;
using ModularPipelines.Modules;
using Shouldly;
using Sourcy.DotNet;

namespace Build.Modules;

/// <summary>
///     Resolve the Release.R* configurations of ToadFlow.Revit.slnx: one add-in build per supported Revit version.
/// </summary>
public sealed class ResolveConfigurationsModule : Module<string[]>
{
    protected override async Task<string[]?> ExecuteAsync(IModuleContext context, CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(Solutions.ToadFlow_Revit.FullName);
        var solution = await SolutionSerializers.SlnXml.OpenAsync(stream, cancellationToken);
        var configurations = solution.BuildTypes
            .Where(configuration => configuration.StartsWith("Release.R", StringComparison.OrdinalIgnoreCase))
            .ToArray();

        configurations.ShouldNotBeEmpty("No Release.R* configurations in ToadFlow.Revit.slnx");
        return configurations;
    }
}
