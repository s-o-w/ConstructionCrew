using Microsoft.Extensions.Configuration;

namespace ConstructionCrew.Config;

/// <summary>
/// Resolves AppSettings for repoRoot. Precedence, lowest to highest: defaults
/// (<see cref="AppSettings.ForRepoRoot"/>), appsettings.json,
/// CONSTRUCTIONCREW_ environment variables, command line.
///
/// Only HomeOfficePort is overridable today, so a second instance (e.g. from a
/// git worktree) can run alongside the live one on a different port. Config
/// and state paths stay derived from repoRoot, which already isolates the two.
/// </summary>
public static class AppSettingsLoader
{
    private static readonly Dictionary<string, string> CommandLineSwitchMappings = new()
    {
        ["--home-office-port"] = "HomeOffice:Port",
        ["--port"] = "HomeOffice:Port",
        ["--vault-root"] = "Vault:Root",
    };

    // AddCommandLine expects every "--switch" to have a value; a bare flag like
    // "--debug" has none, so it silently swallows the next token as its value
    // (e.g. "--debug --port 6202" reads "6202" as --debug's value). Program.cs
    // parses bare flags directly off args, so strip them before AddCommandLine.
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
        var vaultRoot = config.GetValue<string?>("Vault:Root") ?? defaults.VaultRoot;
        var notificationsCommand = config.GetValue<string?>("Notifications:Command") ?? defaults.NotificationsCommand;

        var resolvedVaultRoot = string.IsNullOrWhiteSpace(vaultRoot) ? null : vaultRoot;

        // State lives in the vault when one is configured, so jobs.jsonl and
        // tools.json survive a clean repo clone and sync across machines via
        // Obsidian Sync. Falls back to repoRoot/state/ when no vault is set.
        var stateDirectory = resolvedVaultRoot is not null
            ? Path.Combine(resolvedVaultRoot, "AI", "ConstructionCrew", "state")
            : defaults.StateDirectory;

        return defaults with
        {
            HomeOfficePort = port,
            VaultRoot = resolvedVaultRoot,
            StateDirectory = stateDirectory,
            NotificationsCommand = string.IsNullOrWhiteSpace(notificationsCommand) ? null : notificationsCommand,
        };
    }
}
