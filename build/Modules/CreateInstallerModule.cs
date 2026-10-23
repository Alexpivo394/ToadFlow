using System.Diagnostics;
using Build.Options;
using Microsoft.Extensions.Options;
using ModularPipelines.Attributes;
using ModularPipelines.Context;
using ModularPipelines.DotNet.Extensions;
using ModularPipelines.DotNet.Options;
using ModularPipelines.FileSystem;
using ModularPipelines.Git.Extensions;
using ModularPipelines.Modules;
using ModularPipelines.Options;
using Shouldly;
using Sourcy.DotNet;

namespace Build.Modules;

/// <summary>
///     Create the agent .msi: the published agent and the add-in for every Revit version.
/// </summary>
[DependsOn<ResolveVersioningModule>]
[DependsOn<CompileProjectModule>]
[DependsOn<PublishAgentModule>]
public sealed class CreateInstallerModule(IOptions<BuildOptions> buildOptions) : Module
{
    protected override async Task ExecuteModuleAsync(IModuleContext context, CancellationToken cancellationToken)
    {
        var versioning = (await context.GetModule<ResolveVersioningModule>()).ValueOrDefault!;
        var agentFolder = (await context.GetModule<PublishAgentModule>()).ValueOrDefault!;
        var wixToolFolder = await InstallWixAsync(context, cancellationToken);

        var installer = new ModularPipelines.FileSystem.File(Projects.Installer.FullName);
        await context.DotNet().Build(new DotNetBuildOptions
        {
            ProjectSolution = installer.Path,
            Configuration = "Release"
        }, cancellationToken: cancellationToken);

        var builderFile = installer.Folder!
            .GetFolder("bin")
            .FindFile(file => file.NameWithoutExtension == installer.NameWithoutExtension && file.Extension == ".exe");
        builderFile.ShouldNotBeNull($"No installer builder was found for the project: {installer.NameWithoutExtension}");

        var addinFolders = new ModularPipelines.FileSystem.File(Projects.ToadFlow_Revit_Addin.FullName).Folder!
            .GetFolder("bin")
            .GetFolders(folder => folder.Name == "publish" && folder.Parent!.Name.StartsWith("Release.R", StringComparison.OrdinalIgnoreCase))
            .Select(folder => folder.Path)
            .ToArray();
        addinFolders.ShouldNotBeEmpty("No add-in builds were found to create an installer");

        await context.Shell.Command.ExecuteCommandLineTool(
            new GenericCommandLineToolOptions(builderFile.Path)
            {
                Arguments = [versioning.Version, agentFolder, .. addinFolders]
            },
            new CommandExecutionOptions
            {
                WorkingDirectory = context.Git().RootDirectory,
                EnvironmentVariables = new Dictionary<string, string?>
                {
                    { "PATH", $"{Environment.GetEnvironmentVariable("PATH")};{wixToolFolder}" }
                }
            }, cancellationToken: cancellationToken);

        var outputFolder = context.Git().RootDirectory.GetFolder(buildOptions.Value.OutputDirectory);
        var outputFiles = outputFolder.GetFiles(file => file.Extension == ".msi").ToArray();
        outputFiles.ShouldNotBeEmpty("Failed to create an installer");

        foreach (var outputFile in outputFiles)
        {
            context.Summary.KeyValue("Artifacts", "Installer", outputFile.Path);
        }
    }

    /// <summary>
    ///     Install the WiX toolset required for building installers.
    /// </summary>
    private static async Task<Folder> InstallWixAsync(IModuleContext context, CancellationToken cancellationToken)
    {
        var wixToolFolder = Folder.CreateTemporaryFolder();
        await context.DotNet().Tool.Execute(new DotNetToolOptions
        {
            Arguments = ["install", "wix", "--version", "7.*", "--tool-path", wixToolFolder.Path]
        }, cancellationToken: cancellationToken);

        var wixExe = wixToolFolder.GetFile("wix.exe");
        var wixVersion = FileVersionInfo.GetVersionInfo(wixExe.Path).FileVersion!;

        await context.Shell.Command.ExecuteCommandLineTool(
            new GenericCommandLineToolOptions(wixExe.Path)
            {
                Arguments = ["eula", "accept", "wix7"]
            }, cancellationToken: cancellationToken);

        await context.Shell.Command.ExecuteCommandLineTool(
            new GenericCommandLineToolOptions(wixExe.Path)
            {
                Arguments = ["extension", "add", "-g", $"WixToolset.UI.wixext/{wixVersion}"]
            }, cancellationToken: cancellationToken);

        return wixToolFolder;
    }
}
