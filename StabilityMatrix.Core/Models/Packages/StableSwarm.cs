﻿using System.Diagnostics;
using System.Text.RegularExpressions;
using FreneticUtilities.FreneticDataSyntax;
using StabilityMatrix.Core.Attributes;
using StabilityMatrix.Core.Exceptions;
using StabilityMatrix.Core.Helper;
using StabilityMatrix.Core.Helper.Cache;
using StabilityMatrix.Core.Models.FDS;
using StabilityMatrix.Core.Models.FileInterfaces;
using StabilityMatrix.Core.Models.Progress;
using StabilityMatrix.Core.Processes;
using StabilityMatrix.Core.Services;

namespace StabilityMatrix.Core.Models.Packages;

[Singleton(typeof(BasePackage))]
public class StableSwarm(
    IGithubApiCache githubApi,
    ISettingsManager settingsManager,
    IDownloadService downloadService,
    IPrerequisiteHelper prerequisiteHelper
) : BaseGitPackage(githubApi, settingsManager, downloadService, prerequisiteHelper)
{
    private Process? dotnetProcess;

    public override string Name => "StableSwarmUI";
    public override string RepositoryName => "SwarmUI";
    public override string DisplayName { get; set; } = "SwarmUI";
    public override string Author => "mcmonkeyprojects";
    public override string Blurb =>
        "A Modular Stable Diffusion Web-User-Interface, with an emphasis on making powertools easily accessible, high performance, and extensibility.";

    public override string LicenseType => "MIT";
    public override string LicenseUrl =>
        "https://github.com/mcmonkeyprojects/SwarmUI/blob/master/LICENSE.txt";
    public override string LaunchCommand => string.Empty;
    public override Uri PreviewImageUri =>
        new("https://github.com/mcmonkeyprojects/SwarmUI/raw/master/.github/images/swarmui.jpg");
    public override string OutputFolderName => "Output";
    public override IEnumerable<SharedFolderMethod> AvailableSharedFolderMethods =>
        [SharedFolderMethod.Symlink, SharedFolderMethod.Configuration, SharedFolderMethod.None];
    public override SharedFolderMethod RecommendedSharedFolderMethod => SharedFolderMethod.Configuration;
    public override bool OfferInOneClickInstaller => false;

    public override List<LaunchOptionDefinition> LaunchOptions =>
        [
            new LaunchOptionDefinition
            {
                Name = "Host",
                Type = LaunchOptionType.String,
                DefaultValue = "127.0.0.1",
                Options = ["--host"]
            },
            new LaunchOptionDefinition
            {
                Name = "Port",
                Type = LaunchOptionType.String,
                DefaultValue = "7801",
                Options = ["--port"]
            },
            new LaunchOptionDefinition
            {
                Name = "Ngrok Path",
                Type = LaunchOptionType.String,
                Options = ["--ngrok-path"]
            },
            new LaunchOptionDefinition
            {
                Name = "Ngrok Basic Auth",
                Type = LaunchOptionType.String,
                Options = ["--ngrok-basic-auth"]
            },
            new LaunchOptionDefinition
            {
                Name = "Cloudflared Path",
                Type = LaunchOptionType.String,
                Options = ["--cloudflared-path"]
            },
            new LaunchOptionDefinition
            {
                Name = "Proxy Region",
                Type = LaunchOptionType.String,
                Options = ["--proxy-region"]
            },
            new LaunchOptionDefinition
            {
                Name = "Launch Mode",
                Type = LaunchOptionType.Bool,
                Options = ["--launch-mode web", "--launch-mode webinstall"]
            },
            LaunchOptionDefinition.Extras
        ];

    public override Dictionary<SharedFolderType, IReadOnlyList<string>> SharedFolders =>
        new()
        {
            [SharedFolderType.StableDiffusion] = ["Models/Stable-Diffusion"],
            [SharedFolderType.Lora] = ["Models/Lora"],
            [SharedFolderType.VAE] = ["Models/VAE"],
            [SharedFolderType.TextualInversion] = ["Models/Embeddings"],
            [SharedFolderType.ControlNet] = ["Models/controlnet"],
            [SharedFolderType.InvokeClipVision] = ["Models/clip_vision"]
        };
    public override Dictionary<SharedOutputType, IReadOnlyList<string>> SharedOutputFolders =>
        new() { [SharedOutputType.Text2Img] = [OutputFolderName] };
    public override string MainBranch => "master";
    public override bool ShouldIgnoreReleases => true;
    public override IEnumerable<TorchVersion> AvailableTorchVersions =>
        [TorchVersion.Cpu, TorchVersion.Cuda, TorchVersion.DirectMl, TorchVersion.Rocm, TorchVersion.Mps];
    public override PackageDifficulty InstallerSortOrder => PackageDifficulty.Advanced;
    public override IEnumerable<PackagePrerequisite> Prerequisites =>
        [
            PackagePrerequisite.Git,
            PackagePrerequisite.Dotnet,
            PackagePrerequisite.Python310,
            PackagePrerequisite.VcRedist
        ];

    private FilePath GetSettingsPath(string installLocation) =>
        Path.Combine(installLocation, "Data", "Settings.fds");

    private FilePath GetBackendsPath(string installLocation) =>
        Path.Combine(installLocation, "Data", "Backends.fds");

    public override async Task InstallPackage(
        string installLocation,
        TorchVersion torchVersion,
        SharedFolderMethod selectedSharedFolderMethod,
        DownloadPackageVersionOptions versionOptions,
        IProgress<ProgressReport>? progress = null,
        Action<ProcessOutput>? onConsoleOutput = null
    )
    {
        progress?.Report(new ProgressReport(-1f, "Installing SwarmUI...", isIndeterminate: true));

        var comfy = settingsManager.Settings.InstalledPackages.FirstOrDefault(
            x => x.PackageName == nameof(ComfyUI)
        );

        if (comfy == null)
        {
            throw new InvalidOperationException("ComfyUI must be installed to use SwarmUI");
        }

        try
        {
            await prerequisiteHelper
                .RunDotnet(
                    [
                        "nuget",
                        "add",
                        "source",
                        "https://api.nuget.org/v3/index.json",
                        "--name",
                        "\"NuGet official package source\""
                    ],
                    workingDirectory: installLocation,
                    onProcessOutput: onConsoleOutput
                )
                .ConfigureAwait(false);
        }
        catch (ProcessException e)
        {
            // ignore, probably means the source is already there
        }

        var srcFolder = Path.Combine(installLocation, "src");
        var csprojName = "StableSwarmUI.csproj";
        if (File.Exists(Path.Combine(srcFolder, "SwarmUI.csproj")))
        {
            csprojName = "SwarmUI.csproj";
        }

        await prerequisiteHelper
            .RunDotnet(
                ["build", $"src/{csprojName}", "--configuration", "Release", "-o", "src/bin/live_release"],
                workingDirectory: installLocation,
                onProcessOutput: onConsoleOutput
            )
            .ConfigureAwait(false);

        // set default settings
        var settings = new StableSwarmSettings { IsInstalled = true };

        if (selectedSharedFolderMethod is SharedFolderMethod.Configuration)
        {
            settings.Paths = new StableSwarmSettings.PathsData
            {
                ModelRoot = settingsManager.ModelsDirectory,
                SDModelFolder = Path.Combine(
                    settingsManager.ModelsDirectory,
                    SharedFolderType.StableDiffusion.ToString()
                ),
                SDLoraFolder = Path.Combine(
                    settingsManager.ModelsDirectory,
                    SharedFolderType.Lora.ToString()
                ),
                SDVAEFolder = Path.Combine(settingsManager.ModelsDirectory, SharedFolderType.VAE.ToString()),
                SDEmbeddingFolder = Path.Combine(
                    settingsManager.ModelsDirectory,
                    SharedFolderType.TextualInversion.ToString()
                ),
                SDControlNetsFolder = Path.Combine(
                    settingsManager.ModelsDirectory,
                    SharedFolderType.ControlNet.ToString()
                ),
                SDClipVisionFolder = Path.Combine(
                    settingsManager.ModelsDirectory,
                    SharedFolderType.InvokeClipVision.ToString()
                )
            };
        }

        settings.Save(true).SaveToFile(GetSettingsPath(installLocation));

        var backendsFile = new FDSSection();
        var dataSection = new FDSSection();
        dataSection.Set("type", "comfyui_selfstart");
        dataSection.Set("title", "StabilityMatrix ComfyUI Self-Start");
        dataSection.Set("enabled", true);

        var launchArgs = comfy.LaunchArgs ?? [];
        var comfyArgs = string.Join(
            ' ',
            launchArgs
                .Select(arg => arg.ToArgString()?.TrimEnd())
                .Where(arg => !string.IsNullOrWhiteSpace(arg))
        );

        dataSection.Set(
            "settings",
            new ComfyUiSelfStartSettings
            {
                StartScript = $"../{comfy.DisplayName}/main.py",
                DisableInternalArgs = false,
                AutoUpdate = false,
                ExtraArgs = comfyArgs
            }.Save(true)
        );

        backendsFile.Set("0", dataSection);
        backendsFile.SaveToFile(GetBackendsPath(installLocation));
    }

    public override async Task RunPackage(
        string installedPackagePath,
        string command,
        string arguments,
        Action<ProcessOutput>? onConsoleOutput
    )
    {
        var aspEnvVars = new Dictionary<string, string>
        {
            ["ASPNETCORE_ENVIRONMENT"] = "Production",
            ["ASPNETCORE_URLS"] = "http://*:7801"
        };

        void HandleConsoleOutput(ProcessOutput s)
        {
            onConsoleOutput?.Invoke(s);

            if (s.Text.Contains("Starting webserver", StringComparison.OrdinalIgnoreCase))
            {
                var regex = new Regex(@"(https?:\/\/)([^:\s]+):(\d+)");
                var match = regex.Match(s.Text);
                if (match.Success)
                {
                    WebUrl = match.Value;
                }
                OnStartupComplete(WebUrl);
            }
        }

        var releaseFolder = Path.Combine(installedPackagePath, "src", "bin", "live_release");
        var dllName = "StableSwarmUI.dll";
        if (File.Exists(Path.Combine(releaseFolder, "SwarmUI.dll")))
        {
            dllName = "SwarmUI.dll";
        }

        dotnetProcess = await prerequisiteHelper
            .RunDotnet(
                args: $"{Path.Combine(releaseFolder, dllName)} {arguments.TrimEnd()}",
                workingDirectory: installedPackagePath,
                envVars: aspEnvVars,
                onProcessOutput: HandleConsoleOutput,
                waitForExit: false
            )
            .ConfigureAwait(false);
    }

    public override async Task<bool> CheckForUpdates(InstalledPackage package)
    {
        var needsMigrate = false;
        try
        {
            var output = await prerequisiteHelper
                .GetGitOutput(["remote", "get-url", "origin"], package.FullPath)
                .ConfigureAwait(false);

            if (
                output.StandardOutput != null
                && output.StandardOutput.Contains("Stability", StringComparison.OrdinalIgnoreCase)
            )
            {
                needsMigrate = true;
            }
        }
        catch (Exception)
        {
            needsMigrate = true;
        }

        if (needsMigrate)
        {
            await prerequisiteHelper
                .RunGit(["remote", "set-url", "origin", GithubUrl], package.FullPath)
                .ConfigureAwait(false);
        }

        return await base.CheckForUpdates(package).ConfigureAwait(false);
    }

    public override Task SetupModelFolders(
        DirectoryPath installDirectory,
        SharedFolderMethod sharedFolderMethod
    ) =>
        sharedFolderMethod switch
        {
            SharedFolderMethod.Symlink
                => base.SetupModelFolders(installDirectory, SharedFolderMethod.Symlink),
            SharedFolderMethod.Configuration => SetupModelFoldersConfig(installDirectory), // TODO
            _ => Task.CompletedTask
        };

    public override Task RemoveModelFolderLinks(
        DirectoryPath installDirectory,
        SharedFolderMethod sharedFolderMethod
    ) =>
        sharedFolderMethod switch
        {
            SharedFolderMethod.Symlink => base.RemoveModelFolderLinks(installDirectory, sharedFolderMethod),
            SharedFolderMethod.Configuration => RemoveModelFoldersConfig(installDirectory),
            _ => Task.CompletedTask
        };

    public override async Task WaitForShutdown()
    {
        if (dotnetProcess is { HasExited: false })
        {
            dotnetProcess.Kill(true);
            try
            {
                await dotnetProcess
                    .WaitForExitAsync(new CancellationTokenSource(5000).Token)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException e)
            {
                Console.WriteLine(e);
            }
        }

        dotnetProcess = null;
        GC.SuppressFinalize(this);
    }

    private Task SetupModelFoldersConfig(DirectoryPath installDirectory)
    {
        var settingsPath = GetSettingsPath(installDirectory);
        var existingSettings = new StableSwarmSettings();
        var settingsExists = File.Exists(settingsPath);
        if (settingsExists)
        {
            var section = FDSUtility.ReadFile(settingsPath);
            existingSettings.Load(section);
        }

        existingSettings.Paths = new StableSwarmSettings.PathsData
        {
            ModelRoot = settingsManager.ModelsDirectory,
            SDModelFolder = Path.Combine(
                settingsManager.ModelsDirectory,
                SharedFolderType.StableDiffusion.ToString()
            ),
            SDLoraFolder = Path.Combine(settingsManager.ModelsDirectory, SharedFolderType.Lora.ToString()),
            SDVAEFolder = Path.Combine(settingsManager.ModelsDirectory, SharedFolderType.VAE.ToString()),
            SDEmbeddingFolder = Path.Combine(
                settingsManager.ModelsDirectory,
                SharedFolderType.TextualInversion.ToString()
            ),
            SDControlNetsFolder = Path.Combine(
                settingsManager.ModelsDirectory,
                SharedFolderType.ControlNet.ToString()
            ),
            SDClipVisionFolder = Path.Combine(
                settingsManager.ModelsDirectory,
                SharedFolderType.InvokeClipVision.ToString()
            )
        };

        existingSettings.Save(true).SaveToFile(settingsPath);

        return Task.CompletedTask;
    }

    private Task RemoveModelFoldersConfig(DirectoryPath installDirectory)
    {
        var settingsPath = GetSettingsPath(installDirectory);
        var existingSettings = new StableSwarmSettings();
        var settingsExists = File.Exists(settingsPath);
        if (settingsExists)
        {
            var section = FDSUtility.ReadFile(settingsPath);
            existingSettings.Load(section);
        }

        existingSettings.Paths = new StableSwarmSettings.PathsData();
        existingSettings.Save(true).SaveToFile(settingsPath);

        return Task.CompletedTask;
    }
}
