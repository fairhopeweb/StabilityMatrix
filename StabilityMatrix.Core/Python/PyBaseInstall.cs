﻿using System.Text.Json;
using NLog;
using StabilityMatrix.Core.Exceptions;
using StabilityMatrix.Core.Helper;
using StabilityMatrix.Core.Models;
using StabilityMatrix.Core.Models.FileInterfaces;
using StabilityMatrix.Core.Processes;

namespace StabilityMatrix.Core.Python;

public class PyBaseInstall(DirectoryPath rootPath)
{
    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

    public static PyBaseInstall Default { get; } = new(PyRunner.PythonDir);

    /// <summary>
    /// Root path of the Python installation.
    /// </summary>
    public DirectoryPath RootPath { get; } = rootPath;

    /// <summary>
    /// Whether this is a portable Windows installation.
    /// Path structure is different.
    /// </summary>
    public bool IsWindowsPortable { get; init; }

    private int MajorVersion { get; init; }

    private int MinorVersion { get; init; }

    public FilePath PythonExePath =>
        Compat.Switch(
            (PlatformKind.Windows, RootPath.JoinFile("python.exe")),
            (PlatformKind.Linux, RootPath.JoinFile("bin", "python3")),
            (PlatformKind.MacOS, RootPath.JoinFile("bin", "python3"))
        );

    public string DefaultTclTkPath =>
        Compat.Switch(
            (PlatformKind.Windows, RootPath.JoinFile("tcl", "tcl8.6")),
            (PlatformKind.Linux, RootPath.JoinFile("lib", "tcl8.6")),
            (PlatformKind.MacOS, RootPath.JoinFile("lib", "tcl8.6"))
        );

    /// <summary>
    /// Creates a new virtual environment runner.
    /// </summary>
    /// <param name="venvPath">Root path of the venv</param>
    /// <param name="workingDirectory">Working directory of the venv</param>
    /// <param name="environmentVariables">Extra environment variables to set</param>
    /// <param name="overrideEnvironmentVariables">Extra environment variables to set at the end</param>
    /// <param name="withDefaultTclTkEnv">Whether to include the Tcl/Tk library paths via <see cref="DefaultTclTkPath"/></param>
    public PyVenvRunner CreateVenvRunner(
        DirectoryPath venvPath,
        DirectoryPath? workingDirectory = null,
        IReadOnlyDictionary<string, string>? environmentVariables = null,
        IReadOnlyDictionary<string, string>? overrideEnvironmentVariables = null,
        bool withDefaultTclTkEnv = false
    )
    {
        var runner = new PyVenvRunner(this, venvPath) { WorkingDirectory = workingDirectory };

        if (environmentVariables is { Count: > 0 })
        {
            runner.EnvironmentVariables = runner.EnvironmentVariables.AddRange(environmentVariables);
        }

        if (withDefaultTclTkEnv)
        {
            runner.EnvironmentVariables = runner.EnvironmentVariables.SetItem(
                "TCL_LIBRARY",
                DefaultTclTkPath
            );
            runner.EnvironmentVariables = runner.EnvironmentVariables.SetItem("TK_LIBRARY", DefaultTclTkPath);
        }

        if (overrideEnvironmentVariables is { Count: > 0 })
        {
            runner.EnvironmentVariables = runner.EnvironmentVariables.AddRange(overrideEnvironmentVariables);
        }

        return runner;
    }

    /// <summary>
    /// Creates a new virtual environment runner.
    /// </summary>
    /// <param name="venvPath">Root path of the venv</param>
    /// <param name="workingDirectory">Working directory of the venv</param>
    /// <param name="environmentVariables">Extra environment variables to set</param>
    /// <param name="overrideEnvironmentVariables">Extra environment variables to set at the end</param>
    /// <param name="withDefaultTclTkEnv">Whether to include the Tcl/Tk library paths via <see cref="DefaultTclTkPath"/></param>
    /// <param name="withQueriedTclTkEnv">Whether to include the Tcl/Tk library paths via <see cref="TryQueryTclTkLibraryAsync"/></param>
    public async Task<PyVenvRunner> CreateVenvRunnerAsync(
        DirectoryPath venvPath,
        DirectoryPath? workingDirectory = null,
        IReadOnlyDictionary<string, string>? environmentVariables = null,
        IReadOnlyDictionary<string, string>? overrideEnvironmentVariables = null,
        bool withDefaultTclTkEnv = false,
        bool withQueriedTclTkEnv = false
    )
    {
        var runner = CreateVenvRunner(
            venvPath: venvPath,
            workingDirectory: workingDirectory,
            environmentVariables: environmentVariables,
            overrideEnvironmentVariables: null,
            withDefaultTclTkEnv: withDefaultTclTkEnv
        );

        if (withQueriedTclTkEnv)
        {
            var queryResult = await TryQueryTclTkLibraryAsync().ConfigureAwait(false);
            if (queryResult is { Result: { } result })
            {
                if (!string.IsNullOrEmpty(result.TclLibrary))
                {
                    runner.EnvironmentVariables = runner.EnvironmentVariables.SetItem(
                        "TCL_LIBRARY",
                        result.TclLibrary
                    );
                }
                if (!string.IsNullOrEmpty(result.TkLibrary))
                {
                    runner.EnvironmentVariables = runner.EnvironmentVariables.SetItem(
                        "TK_LIBRARY",
                        result.TkLibrary
                    );
                }
            }
            else
            {
                Logger.Error(queryResult.Exception, "Failed to query Tcl/Tk library paths");
            }
        }

        if (overrideEnvironmentVariables is { Count: > 0 })
        {
            runner.EnvironmentVariables = runner.EnvironmentVariables.AddRange(overrideEnvironmentVariables);
        }

        return runner;
    }

    public async Task<TaskResult<QueryTclTkLibraryResult>> TryQueryTclTkLibraryAsync()
    {
        var processResult = await QueryTclTkLibraryPathAsync().ConfigureAwait(false);

        if (!processResult.IsSuccessExitCode || string.IsNullOrEmpty(processResult.StandardOutput))
        {
            return TaskResult<QueryTclTkLibraryResult>.FromException(new ProcessException(processResult));
        }

        try
        {
            var result = JsonSerializer.Deserialize(
                processResult.StandardOutput,
                QueryTclTkLibraryResultJsonContext.Default.QueryTclTkLibraryResult
            );

            return new TaskResult<QueryTclTkLibraryResult>(result!);
        }
        catch (JsonException e)
        {
            return TaskResult<QueryTclTkLibraryResult>.FromException(e);
        }
    }

    private async Task<ProcessResult> QueryTclTkLibraryPathAsync()
    {
        const string script = """
                              import tkinter
                              import json
                              
                              root = tkinter.Tk()
                              
                              print(json.dumps({
                                  'TclLibrary': root.tk.exprstring('$tcl_library'),
                                  'TkLibrary': root.tk.exprstring('$tk_library')
                              }))
                              """;

        return await ProcessRunner.GetProcessResultAsync(PythonExePath, ["-c", script]).ConfigureAwait(false);
    }
}
