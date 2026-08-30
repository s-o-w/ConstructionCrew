using System.Text.Json;
using System.Text.Json.Nodes;
using ConstructionCrew.Config;
using ConstructionCrew.Core.Models;
using ConstructionCrew.Providers;
using Spectre.Console;

namespace ConstructionCrew.App;

/// <summary>
/// First run: there is no foremen.yaml yet, so there is no GC, so there is
/// nothing for the Boss to talk to. This wizard produces one.
///
/// It hooks off <c>File.Exists(settings.ForemenConfigPath)</c> in Program.cs,
/// deliberately NOT off the "no Foreman named GC" branch. Those are two
/// different failures: a missing file is a fresh install, while a config that
/// loads and has no GC-named entry is a broken roster, and the second stays a
/// hard fail.
///
/// Flow: point at an existing Vault or scaffold a new one -> recognize the
/// layout -> hire GC -> write foremen.yaml -> persist the Vault path to
/// appsettings.json. The resolved Vault path is RETURNED so Program.cs can do
/// <c>settings = settings with { VaultRoot = resolved }</c> on this same run --
/// AppSettings is a record and nothing reloads it from disk, so without that
/// reassignment the just-written <c>${vaultRoot}</c> would fail to expand and
/// /hire's Vault guard would still see null.
/// </summary>
public static class FirstRunWizard
{
    /// <summary>
    /// Runs the first-run flow. Returns the resolved absolute Vault path, or
    /// null if the Boss backed out (in which case nothing was written).
    /// </summary>
    public static string? Run(string repoRoot, AppSettings settings, IReadOnlyList<string> availableProviderIds)
    {
        AnsiConsole.Write(new Rule("[bold yellow]first run[/]").LeftJustified());
        AnsiConsole.MarkupLine(
            "[grey]No Foreman roster yet. Let's set up your Vault and hire the GC.[/]");
        AnsiConsole.WriteLine();

        if (availableProviderIds.Count == 0)
        {
            AnsiConsole.MarkupLine(
                "[red]No working CLI providers are installed -- there's nothing to hire a GC with.[/] " +
                "Install one (claude, codex, or copilot) and start again.");
            return null;
        }

        var vaultRoot = ResolveVaultRoot(repoRoot);
        if (vaultRoot is null)
        {
            return null;
        }

        ReportRecognition(vaultRoot);

        // Phase 1c's reachability check, run at the one moment a Vault path is
        // first known. Never repairs the link itself.
        if (!VaultSkills.ConfirmContinueOnMiss(VaultSkills.Probe(vaultRoot), Console.Out, Console.In))
        {
            AnsiConsole.MarkupLine("[yellow]Stopped -- nothing was written.[/]");
            return null;
        }

        var provider = availableProviderIds.Count == 1
            ? availableProviderIds[0]
            : AnsiConsole.Prompt(
                new SelectionPrompt<string>()
                    .Title("[bold]Engine[/] -- which CLI backs the GC?")
                    .AddChoices(availableProviderIds));

        var displayName = AnsiConsole.Prompt(
            new TextPrompt<string>("[bold]Display name[/] for the GC (optional, blank to just call it 'GC'):")
                .AllowEmpty());

        var instructionsFilePath = EnsureGcInstructions(repoRoot);

        var config = BuildGcConfig(
            settings.GcForemanName,
            provider,
            vaultRoot,
            instructionsFilePath,
            repoRoot,
            string.IsNullOrWhiteSpace(displayName) ? null : displayName.Trim());

        AnsiConsole.Write(new Panel(new Rows(
                new Markup($"[bold]Vault:[/] {Markup.Escape(vaultRoot)}"),
                new Markup($"[bold]GC name:[/] {Markup.Escape(config.Name)} (reserved)"),
                new Markup($"[bold]Display name:[/] {Markup.Escape(config.DisplayName ?? "-")}"),
                new Markup($"[bold]Engine:[/] {Markup.Escape(config.Provider)}")))
            .Header("confirm")
            .Border(BoxBorder.Rounded));

        if (!AnsiConsole.Confirm("Hire this GC?", true))
        {
            AnsiConsole.MarkupLine("[yellow]Cancelled -- nothing was written.[/]");
            return null;
        }

        ForemanConfigWriter.AppendForeman(settings.ForemenConfigPath, config, repoRoot, vaultRoot);
        PersistVaultRoot(Path.Combine(repoRoot, "appsettings.json"), vaultRoot);

        AnsiConsole.MarkupLine($"[bold green]GC hired.[/] Roster written to {Markup.Escape(settings.ForemenConfigPath)}.");
        AnsiConsole.MarkupLine($"[grey]Vault root saved to appsettings.json: {Markup.Escape(vaultRoot)}[/]");
        AnsiConsole.Markup("[grey]Press enter to continue...[/]");
        Console.ReadLine();

        return vaultRoot;
    }

    /// <summary>
    /// The GC's ForemanConfig, exactly as first run writes it. Three things are
    /// invariant and not the Boss's to change here:
    /// - <c>Name</c> is always the reserved GcForemanName. The loader's
    ///   collection-level validation rejects any other spelling.
    /// - <c>Role</c> is always <see cref="CrewRole.GC"/>.
    /// - <c>WorkingDirectory</c> is the VAULT, not this repo -- a CLI auto-loads
    ///   the CLAUDE.md/AGENTS.md in its own cwd, and GC only inherits the
    ///   vault's authoring conventions if the vault is its cwd. This repo comes
    ///   along as an AddDirs entry instead.
    /// <c>DisplayName</c> is the one optional field: UI only, never a lookup key.
    /// </summary>
    public static ForemanConfig BuildGcConfig(
        string gcForemanName,
        string provider,
        string vaultRoot,
        string instructionsFilePath,
        string repoRoot,
        string? displayName) =>
        new(
            gcForemanName,
            CrewRole.GC,
            provider,
            vaultRoot,
            instructionsFilePath,
            new Dictionary<string, string>(ProviderDefaults.GcToolPolicy(provider)),
            JobsiteName: null,
            DisplayName: displayName,
            AddDirs: [repoRoot]);

    /// <summary>
    /// Writes Vault:Root into appsettings.json, merging into whatever is already
    /// there rather than rewriting the file -- HomeOffice:Port lives in the same
    /// file and must survive. A malformed or missing file is replaced with a
    /// fresh document; anything else would leave first run unable to finish.
    /// </summary>
    public static void PersistVaultRoot(string appSettingsPath, string vaultRoot)
    {
        JsonObject root;
        try
        {
            root = File.Exists(appSettingsPath)
                ? JsonNode.Parse(File.ReadAllText(appSettingsPath)) as JsonObject ?? new JsonObject()
                : new JsonObject();
        }
        catch (JsonException)
        {
            root = new JsonObject();
        }

        if (root["Vault"] is not JsonObject vault)
        {
            vault = new JsonObject();
            root["Vault"] = vault;
        }

        vault["Root"] = vaultRoot;

        Directory.CreateDirectory(Path.GetDirectoryName(appSettingsPath)!);
        File.WriteAllText(appSettingsPath, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    }

    /// <summary>Prompts for an existing Vault or scaffolds a new one. Null means the Boss backed out.</summary>
    private static string? ResolveVaultRoot(string repoRoot)
    {
        const string useExisting = "point at an existing Vault";
        const string scaffoldNew = "scaffold a new Vault";

        var choice = AnsiConsole.Prompt(
            new SelectionPrompt<string>()
                .Title("[bold]Vault[/] -- where does the crew's knowledge live?")
                .AddChoices(useExisting, scaffoldNew));

        if (choice == useExisting)
        {
            var existing = AnsiConsole.Prompt(
                new TextPrompt<string>("[bold]Vault path[/] -- an existing directory:")
                    .Validate(p => Directory.Exists(ExpandPath(p))
                        ? ValidationResult.Success()
                        : ValidationResult.Error($"'{p}' doesn't exist.")));

            return ExpandPath(existing);
        }

        var target = AnsiConsole.Prompt(
            new TextPrompt<string>("[bold]New Vault path[/] -- created if it isn't there yet:")
                .Validate(p => string.IsNullOrWhiteSpace(p)
                    ? ValidationResult.Error("Path can't be empty.")
                    : ValidationResult.Success()));

        var vaultRoot = ExpandPath(target);
        var scaffoldSource = VaultLayout.ScaffoldSourceDirectory(repoRoot);

        try
        {
            var written = VaultLayout.Scaffold(scaffoldSource, vaultRoot);
            AnsiConsole.MarkupLine($"[green]Scaffolded {written.Count} file(s)[/] into {Markup.Escape(vaultRoot)}.");
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[red]Could not scaffold the Vault:[/] {Markup.Escape(ex.Message)}");
            return null;
        }

        return vaultRoot;
    }

    /// <summary>
    /// Absolute path from what the Boss typed. "~/Vault" is the shape a Linux or
    /// Mac user reaches for first, and Path.GetFullPath does not expand it -- it
    /// would produce a literal "~" directory next to the cwd.
    /// </summary>
    internal static string ExpandPath(string input)
    {
        var trimmed = input.Trim();

        if (trimmed == "~" || trimmed.StartsWith("~/", StringComparison.Ordinal) || trimmed.StartsWith(@"~\", StringComparison.Ordinal))
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            trimmed = trimmed.Length <= 1 ? home : Path.Combine(home, trimmed[2..]);
        }

        return Path.GetFullPath(trimmed);
    }

    private static void ReportRecognition(string vaultRoot)
    {
        var missing = VaultLayout.MissingMarkers(vaultRoot);

        if (missing.Count == 0)
        {
            AnsiConsole.MarkupLine("[green]Recognized vault layout[/] -- HOME.md, CLAUDE.md, Notes/, Plans/ all present.");
            return;
        }

        AnsiConsole.MarkupLine(
            $"[yellow]Unrecognized vault layout[/] -- missing {Markup.Escape(string.Join(", ", missing))}. " +
            "That's fine; /hire will just ask you for each Foreman's vault folders instead of deriving them.");
    }

    /// <summary>
    /// GC's instructions file ships with this repo. If a clone is missing it,
    /// write a minimal stand-in rather than failing the whole first run -- the
    /// loader hard-fails on an instructionsFilePath that isn't there.
    /// </summary>
    private static string EnsureGcInstructions(string repoRoot)
    {
        var instructionsDir = Path.Combine(repoRoot, "config", "instructions");
        Directory.CreateDirectory(instructionsDir);
        var path = Path.Combine(instructionsDir, "GC.md");

        if (!File.Exists(path))
        {
            File.WriteAllText(
                path,
                """
                # You are the General Contractor (GC)

                You work for the Boss inside ConstructionCrew. You do not write code or
                run shell commands yourself -- your job is to understand what the Boss
                wants, turn it into a clear plan, and dispatch pieces of that plan to the
                right Foreman.

                Call `list_jobsites()` and `list_foremen()` before proposing who should do
                what; both change at runtime. If a jobsite has no assigned Foreman, tell
                the Boss to hire one (`/hire`) before you can dispatch work there.
                """);
        }

        return path;
    }
}
