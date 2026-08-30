using Microsoft.Extensions.Configuration;

namespace ConstructionCrew.Config;

/// <summary>
/// Resolves AppSettings for repoRoot, layering appsettings.json and the
/// environment over the hardcoded defaults, then a command-line override on
/// top. Precedence, lowest to highest: defaults (<see cref="AppSettings.ForRepoRoot"/>),
/// appsettings.json, CONSTRUCTIONCREW_ environment variables, command line.
///
/// Only HomeOfficePort is exposed as an override today -- it is what lets a
/// second instance (for example one built from a git worktree, to test a code
/// change without disturbing the live TUI session) run alongside the first on
/// a different port. Config and state paths stay derived from repoRoot, which
/// already isolates two instances built to two different output directories.
/// </summary>
public static class AppSettingsLoader
{
    private static readonly Dictionary<string, string> CommandLineSwitchMappings = new()
    {
        ["--home-office-port"] = "HomeOffice:Port",
        ["--port"] = "HomeOffice:Port",
    };

    // Microsoft.Extensions.Configuration.CommandLine expects every "--switch" to
    // be followed by its value; a bare flag like "--debug" has none, so it
    // silently swallows the next token as its own value (breaking whatever came
    // after it, e.g. "--debug --port 6202" reads "--port" as --debug's value and
    // "6202" as a stray positional). Bare flags are parsed directly off args in
    // Program.cs instead, so strip them here before handing args to AddCommandLine.
    private static readonly string[] BareFlags = ["--debug"];

    public static AppSettings Load(string repoRoot, string[] args)
    {
        var defaults = AppSettings.ForRepoRoot(repoRoot);

        var argsForConfig = args.Where(a => !BareFlags.Contains(a, StringComparer.OrdinalIgnoreCase)).ToArray();

        var config = new ConfigurationBuilder()
            .AddJsonFile(Path.Combine(repoRoot, "appsettings.json"), optional: true, reloadOnChange: false)
            .AddEnvironmentVariables("CONSTRUCTIONCREW_")
            .AddCommandLine(argsForConfig, CommandLineSwitchMappings)
            .Build();

        var port = config.GetValue<int?>("HomeOffice:Port") ?? defaults.HomeOfficePort;

        return defaults with { HomeOfficePort = port };
    }
}
