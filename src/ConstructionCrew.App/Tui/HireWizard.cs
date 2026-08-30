using ConstructionCrew.Config;
using ConstructionCrew.Core.Models;
using ConstructionCrew.Providers;
using Spectre.Console;

namespace ConstructionCrew.App.Tui;

/// <summary>
/// The Identity/Workspace/Engine/Briefing flow, adapted from Munder Difflin's
/// "Add Agent" dialog. Runs as a plain blocking sequence of Spectre prompts --
/// not part of the redrawn dashboard -- since it's a distinct, linear flow.
/// Workspace here means picking (or creating) the Jobsite this Foreman is
/// strictly assigned to -- one Foreman per Jobsite.
/// </summary>
public static class HireWizard
{
    public static ForemanConfig? Run(
        ForemanDirectory foremen,
        JobsiteDirectory jobsites,
        IReadOnlyList<string> availableProviderIds,
        string repoRoot,
        string vaultRoot,
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> mcpOptionsByProvider)
    {
        AnsiConsole.Write(new Rule("[bold yellow]hire a foreman[/]").LeftJustified());

        // 1. Identity
        var name = AnsiConsole.Prompt(
            new TextPrompt<string>("[bold]Name[/] this Foreman:")
                .Validate(n =>
                {
                    if (string.IsNullOrWhiteSpace(n))
                    {
                        return ValidationResult.Error("Name can't be empty.");
                    }

                    if (n.Equals("GC", StringComparison.OrdinalIgnoreCase))
                    {
                        return ValidationResult.Error("'GC' is reserved.");
                    }

                    return foremen.Find(n) is not null
                        ? ValidationResult.Error($"'{n}' is already hired.")
                        : ValidationResult.Success();
                }));

        // 2. Workspace -- pick or create the one Jobsite this Foreman owns
        var jobsite = PickOrCreateJobsite(jobsites, foremen, repoRoot, vaultRoot);
        if (jobsite is null)
        {
            AnsiConsole.MarkupLine("[yellow]Cancelled -- no jobsite.[/]");
            return null;
        }

        // 3. Engine
        if (availableProviderIds.Count == 0)
        {
            AnsiConsole.MarkupLine("[red]No working CLI providers are registered -- can't hire anyone.[/]");
            return null;
        }

        var provider = AnsiConsole.Prompt(
            new SelectionPrompt<string>()
                .Title("[bold]Engine[/] -- which CLI backs this Foreman?")
                .AddChoices(availableProviderIds));

        // 4. Briefing
        AnsiConsole.MarkupLine("[bold]Briefing[/] -- describe this Foreman's role and goal. Blank line to finish:");
        var briefingLines = new List<string>();
        while (true)
        {
            var line = Console.ReadLine();
            if (string.IsNullOrEmpty(line))
            {
                break;
            }

            briefingLines.Add(line);
        }

        var briefing = briefingLines.Count > 0
            ? string.Join('\n', briefingLines)
            : $"You are the {name} Foreman.";

        // 5. Vault write scope. Last, so a Boss who backs out at the Engine step
        // is never asked for it. A Jobsite created moments ago already carries it
        // (derived there, where the name is first known), so a recognized layout
        // never asks twice. An existing Jobsite from before this field existed,
        // or an unrecognized vault layout, falls through to the prompt.
        var vaultFolders = jobsite.VaultFolders is { Count: > 0 } existingFolders
            ? existingFolders
            : DeriveVaultFolders(vaultRoot, jobsite.Name) ?? PromptForVaultFolders(jobsite.Name);

        // Confirm
        var jobsiteColorName = jobsite.ColorName ?? "grey";
        var jobsiteLine = new Columns(
            new Markup($"[bold]Jobsite:[/] {Markup.Escape(jobsite.Name)} ({Markup.Escape(jobsite.RepoPath)}) -- "),
            new Markup(jobsiteColorName, new Style(foreground: JobsiteColors.Resolve(jobsite.ColorName))));

        AnsiConsole.Write(new Panel(new Rows(
                new Markup($"[bold]Name:[/] {Markup.Escape(name)}"),
                jobsiteLine,
                new Markup($"[bold]Engine:[/] {Markup.Escape(provider)}"),
                new Markup($"[bold]Vault folders:[/] {Markup.Escape(vaultFolders.Count > 0 ? string.Join(", ", vaultFolders) : "(none)")}"),
                new Markup($"[bold]Briefing:[/] {Markup.Escape(Truncate(briefing, 200))}")))
            .Header("confirm")
            .Border(BoxBorder.Rounded));

        if (!AnsiConsole.Confirm("Spawn this Foreman?", true))
        {
            AnsiConsole.MarkupLine("[yellow]Cancelled.[/]");
            return null;
        }

        var instructionsDir = Path.Combine(repoRoot, "config", "instructions");
        Directory.CreateDirectory(instructionsDir);
        var instructionsFilePath = Path.Combine(instructionsDir, $"{name}.md");
        File.WriteAllText(instructionsFilePath, ComposeInstructions(name, briefing, jobsite));

        // Tool policy is provider-specific -- Claude Code's "Bash,Edit,Read,Write" means
        // nothing to Copilot, and Codex has no tool allowlist at all.
        var providerOptions = new Dictionary<string, string>(ProviderDefaults.ToolPolicy(provider));
        if (mcpOptionsByProvider.TryGetValue(provider, out var mcpOptions))
        {
            foreach (var option in mcpOptions)
            {
                providerOptions[option.Key] = option.Value;
            }
        }

        var config = new ForemanConfig(
            name,
            CrewRole.Foreman,
            provider,
            jobsite.RepoPath,
            instructionsFilePath,
            providerOptions,
            jobsite.Name,
            DisplayName: null,
            // Every Foreman gets the Vault on --add-dir: its cwd is its Jobsite
            // repo, so the Vault is otherwise unreachable for reads or sitreps.
            // AddDirs is the absolute read scope; VaultFolders below is the
            // vault-relative WRITE scope, and the two are not the same thing.
            AddDirs: [vaultRoot],
            VaultFolders: vaultFolders);

        var foremenYamlPath = Path.Combine(repoRoot, "config", "foremen.yaml");
        ForemanConfigWriter.AppendForeman(foremenYamlPath, config, repoRoot, vaultRoot);
        foremen.Add(config);

        AnsiConsole.MarkupLine($"[bold green]{Markup.Escape(name)} is hired[/] for jobsite '{Markup.Escape(jobsite.Name)}'. Saved to config/foremen.yaml.");
        return config;
    }

    /// <summary>
    /// The vault-relative folders a Foreman on <paramref name="jobsiteName"/> owns,
    /// derived from the vault's own layout. A recognized layout (HOME.md, CLAUDE.md,
    /// Notes/, Plans/) means the convention holds and the paths are knowable without
    /// asking. Null means "unrecognized -- ask the Boss instead", which is a different
    /// answer from an empty list.
    /// </summary>
    public static IReadOnlyList<string>? DeriveVaultFolders(string vaultRoot, string jobsiteName) =>
        VaultLayout.Recognize(vaultRoot) == VaultRecognition.Recognized
            ? [$"Notes/{jobsiteName}", $"Plans/{jobsiteName}"]
            : null;

    private static IReadOnlyList<string> PromptForVaultFolders(string jobsiteName)
    {
        AnsiConsole.MarkupLine(
            $"[bold]Vault folders[/] -- vault-relative paths this Foreman may write into for " +
            $"'{Markup.Escape(jobsiteName)}' (e.g. [grey]Notes/{Markup.Escape(jobsiteName)}[/]). " +
            "One per line, blank line to finish:");

        var folders = new List<string>();
        while (true)
        {
            var line = Console.ReadLine();
            if (string.IsNullOrWhiteSpace(line))
            {
                break;
            }

            // Vault-relative, always: an absolute path here would silently
            // escape the Vault when it is later combined with VaultRoot.
            folders.Add(line.Trim().Replace('\\', '/').TrimStart('/'));
        }

        return folders;
    }

    private static JobsiteConfig? PickOrCreateJobsite(JobsiteDirectory jobsites, ForemanDirectory foremen, string repoRoot, string vaultRoot)
    {
        const string addNew = "+ add a new jobsite";
        var assignedNames = new HashSet<string>(
            foremen.All().Select(f => f.JobsiteName).Where(n => n is not null)!,
            StringComparer.OrdinalIgnoreCase);

        var unassigned = jobsites.All().Where(j => !assignedNames.Contains(j.Name)).ToList();
        var choices = unassigned.Select(j => j.Name).Append(addNew).ToList();

        var picked = AnsiConsole.Prompt(
            new SelectionPrompt<string>()
                .Title("[bold]Workspace[/] -- pick a jobsite, or add a new one:")
                .AddChoices(choices));

        if (picked != addNew)
        {
            return jobsites.Find(picked);
        }

        var jobsiteName = AnsiConsole.Prompt(
            new TextPrompt<string>("[bold]Jobsite name[/] (e.g. the repo's name):")
                .Validate(n => string.IsNullOrWhiteSpace(n)
                    ? ValidationResult.Error("Name can't be empty.")
                    : jobsites.Find(n) is not null
                        ? ValidationResult.Error($"Jobsite '{n}' already exists.")
                        : ValidationResult.Success()));

        var repoPath = AnsiConsole.Prompt(
            new TextPrompt<string>("[bold]Repo path[/] -- an existing local clone:")
                .Validate(p => Directory.Exists(p)
                    ? ValidationResult.Success()
                    : ValidationResult.Error($"'{p}' doesn't exist. Clone the repo first, then hire.")));

        var repoUrl = AnsiConsole.Prompt(
            new TextPrompt<string>("[bold]Repo URL[/] (optional, blank to skip):").AllowEmpty());

        AnsiConsole.MarkupLine("[bold]Description[/] -- what this jobsite is, for the Foreman's context. Blank line to finish:");
        var descriptionLines = new List<string>();
        while (true)
        {
            var line = Console.ReadLine();
            if (string.IsNullOrEmpty(line))
            {
                break;
            }

            descriptionLines.Add(line);
        }

        const string randomColorChoice = "surprise me (random)";
        var colorChoices = new List<string> { randomColorChoice };
        colorChoices.AddRange(JobsiteColorPalette.Names);
        var colorPick = AnsiConsole.Prompt(
            new SelectionPrompt<string>()
                .Title("[bold]Color[/] -- this jobsite's border color, wherever it shows up:")
                .AddChoices(colorChoices));
        var colorName = colorPick == randomColorChoice ? JobsiteColorPalette.PickRandom(Random.Shared) : colorPick;

        var jobsite = new JobsiteConfig(
            jobsiteName,
            repoPath,
            descriptionLines.Count > 0 ? string.Join('\n', descriptionLines) : string.Empty,
            string.IsNullOrWhiteSpace(repoUrl) ? null : repoUrl,
            colorName,
            // Derived here, where the Jobsite name is first known, so a
            // recognized layout never has to ask. An unrecognized layout leaves
            // this null and Run() prompts once, on the Foreman.
            DeriveVaultFolders(vaultRoot, jobsiteName));

        var jobsitesYamlPath = Path.Combine(repoRoot, "config", "jobsites.yaml");
        JobsiteConfigWriter.AppendJobsite(jobsitesYamlPath, jobsite, repoRoot);
        jobsites.Add(jobsite);

        return jobsite;
    }

    private static string ComposeInstructions(string name, string briefing, JobsiteConfig jobsite)
    {
        return $"""
            {briefing}

            ---

            ## Your jobsite: {jobsite.Name}

            {jobsite.Description}

            Your working directory is this jobsite's repo clone. You do the work
            directly for straightforward tasks. For a well-defined, self-contained
            piece of work, you may instead call `spawn_worker(foreman="{name}", task, engine?)`
            to hand it to an ephemeral Worker -- in your own engine by default, or a
            different one (`engine`) if the task can run to completion non-interactively.
            A Worker may call `ask_foreman(foreman="{name}", question)` if it gets stuck;
            expect to be re-invoked to answer.
            """;
    }

    private static string Truncate(string text, int max) => text.Length > max ? text[..max] + "..." : text;
}
