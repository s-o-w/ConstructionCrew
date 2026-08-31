using ConstructionCrew.Config;
using ConstructionCrew.Core;
using ConstructionCrew.Core.Abstractions;
using ConstructionCrew.Core.Models;
using ConstructionCrew.HomeOffice;
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
    public static async Task<ForemanConfig?> Run(
        ForemanDirectory foremen,
        JobsiteDirectory jobsites,
        JobRegistry jobRegistry,
        IReadOnlyList<string> availableProviderIds,
        string repoRoot,
        string vaultRoot,
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> mcpOptionsByProvider,
        ICliProcessRunner runner,
        CancellationToken cancellationToken)
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
        var jobsite = await PickOrCreateJobsite(jobsites, foremen, repoRoot, vaultRoot, runner, cancellationToken);
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

        // Vault folders first, so the paths shown on the confirm panel a moment
        // ago all resolve on disk by the time the hire reports success.
        var rejectedFolders = EnsureVaultFolders(vaultRoot, vaultFolders);
        var createdFolders = vaultFolders.Except(rejectedFolders, StringComparer.Ordinal).ToList();
        if (createdFolders.Count > 0)
        {
            AnsiConsole.MarkupLine(
                $"[green]Vault folders ready:[/] {Markup.Escape(string.Join(", ", createdFolders))}");
        }

        foreach (var rejected in rejectedFolders)
        {
            AnsiConsole.MarkupLine(
                $"[yellow]Vault folder '{Markup.Escape(rejected)}' resolves outside the vault -- not created.[/] " +
                "It stays in this Foreman's write scope, but nothing will be able to write there.");
        }

        var instructionsDir = Path.Combine(vaultRoot, "AI", "ConstructionCrew", "Instructions");
        Directory.CreateDirectory(instructionsDir);
        var instructionsFilePath = Path.Combine(instructionsDir, $"{name}.md");

        // The briefing is kept verbatim beside the rendered file. Nothing else on
        // disk holds it -- ForemanConfig has no briefing field -- so without this
        // sidecar a later re-render would have to ask the Boss for it again.
        File.WriteAllText(InstructionsComposer.BriefingFilePath(vaultRoot, name), briefing);

        File.WriteAllText(
            instructionsFilePath,
            InstructionsComposer.Compose(
                name,
                CrewRole.Foreman,
                briefing,
                jobsite,
                vaultFolders,
                availableProviderIds,
                vaultRoot));

        // Tool policy is provider-specific -- Claude Code's "Bash,Edit,Read,Write" means
        // nothing to Copilot, and Codex has no tool allowlist at all. Same composer the
        // provider switch in ForemanDetailsCommand uses, so the two cannot drift.
        var providerOptions = ProviderDefaults.ComposeProviderOptions(CrewRole.Foreman, provider, mcpOptionsByProvider);

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

        // The sitewalk. Ordinary dispatch, deliberately: StartJob is the same path
        // the GC uses, so the job shows on the board, carries its own job id, and
        // reports back through file_sitrep like any other work. A hire that
        // declines the sitewalk is still a completed hire.
        if (AnsiConsole.Confirm($"Run {name}'s sitewalk now?", true))
        {
            try
            {
                var jobId = jobRegistry.StartJob(config.Name, SitewalkPrompt(config.Name, jobsite.Name));
                AnsiConsole.MarkupLine(
                    $"[green]Sitewalk dispatched[/] -- job {Markup.Escape(jobId)}. " +
                    $"{Markup.Escape(name)} reports back to the GC when it lands.");
            }
            catch (Exception ex)
            {
                // A failed dispatch never un-hires anyone: the config is already
                // written, and the Boss can dispatch the sitewalk by hand.
                AnsiConsole.MarkupLine(
                    $"[yellow]Sitewalk could not be dispatched:[/] {Markup.Escape(ex.Message)} " +
                    "The hire itself is saved -- ask the GC to dispatch the sitewalk later.");
            }
        }

        return config;
    }

    /// <summary>
    /// The dispatched task text for a sitewalk. A POINTER, not the brief: the
    /// sitewalk brief itself is template text in AI/ConstructionCrew/Templates/foreman-instructions.md,
    /// rendered into this Foreman's instructions file by InstructionsComposer and
    /// prepended by LocalCliAgent on turn one. Writing the brief here too would
    /// mean maintaining it in two places and letting the two drift.
    /// </summary>
    public static string SitewalkPrompt(string foremanName, string jobsiteName) =>
        $"Run your sitewalk on jobsite '{jobsiteName}' now, exactly as the \"The sitewalk\" section of your " +
        "instructions describes it: read-only survey of the code, the backlog and the docs; findings written to " +
        $"Notes/{jobsiteName}/Sitewalk.md; then the kind=\"milestone\" file_sitrep that tells the GC; then " +
        $"build_graph as the closing step. You are {foremanName}. Change no code and open no PR on this job.";

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

    /// <summary>
    /// Creates the Foreman's vault folders at hire time. Not required for
    /// correctness (SitrepWriter and every agent Write already create parents) --
    /// this exists so an overridden or mistyped path is visible in the vault the
    /// moment the hire completes, rather than at the first write. Any entry that
    /// resolves outside vaultRoot is skipped and reported, never created.
    /// </summary>
    internal static IReadOnlyList<string> EnsureVaultFolders(string vaultRoot, IReadOnlyList<string> folders)
    {
        var rejected = new List<string>();

        if (string.IsNullOrWhiteSpace(vaultRoot) || folders.Count == 0)
        {
            return rejected;
        }

        var vaultRootFull = Path.GetFullPath(vaultRoot);
        var rootWithSeparator = vaultRootFull.EndsWith(Path.DirectorySeparatorChar)
            ? vaultRootFull
            : vaultRootFull + Path.DirectorySeparatorChar;

        foreach (var folder in folders)
        {
            if (string.IsNullOrWhiteSpace(folder))
            {
                continue;
            }

            var full = Path.GetFullPath(Path.Combine(vaultRootFull, folder));

            // The same prefix guard SitrepWriter.RequireInside uses: an entry
            // carrying ".." (or an absolute path) resolves out of the vault and
            // is refused rather than created somewhere unexpected.
            if (!full.StartsWith(rootWithSeparator, PathComparison.ForPathPrefix))
            {
                rejected.Add(folder);
                continue;
            }

            Directory.CreateDirectory(full);
        }

        return rejected;
    }

    /// <summary>Blank means "main" -- never the literal fallback prose. That token
    /// lands inside a `gh pr create --base ...` command.</summary>
    internal static string NormalizeBranch(string? input) =>
        string.IsNullOrWhiteSpace(input) ? "main" : input.Trim();

    /// <summary>Blank stays null so InstructionsComposer's "ask the Boss" prose
    /// renders -- that fallback is correct when the command is genuinely unknown.</summary>
    internal static string? NormalizeOptionalCommand(string? input) =>
        string.IsNullOrWhiteSpace(input) ? null : input.Trim();

    /// <summary>
    /// The derived Notes/&lt;Jobsite&gt; + Plans/&lt;Jobsite&gt; default is a DEFAULT, not a
    /// rule -- real projects live elsewhere in a vault. Show it and let the Boss take
    /// it or replace it. An unrecognized layout has nothing to show and asks outright.
    /// </summary>
    internal static IReadOnlyList<string> ResolveVaultFolders(string vaultRoot, string jobsiteName)
    {
        var derived = DeriveVaultFolders(vaultRoot, jobsiteName);
        if (derived is null)
        {
            return PromptForVaultFolders(jobsiteName);
        }

        AnsiConsole.MarkupLine(
            $"[bold]Vault folders[/] -- where '{Markup.Escape(jobsiteName)}' lives in the vault. " +
            $"Default: [grey]{Markup.Escape(string.Join(", ", derived))}[/]");

        return AnsiConsole.Confirm("Use these?", true)
            ? derived
            : PromptForVaultFolders(jobsiteName);
    }

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

    /// <summary>
    /// Typed at any free-text prompt inside <see cref="PickOrCreateJobsite"/> to
    /// bail out of hiring entirely. Spectre's <c>TextPrompt.Validate</c> has no
    /// escape of its own -- a rejected answer just re-prompts forever -- so the
    /// two validated prompts here (Jobsite name, Repo path) are hand-rolled loops
    /// instead, specifically so a real cancel path exists.
    /// </summary>
    private const string CancelSentinel = "cancel";

    internal static bool IsCancel(string? input) =>
        input is not null && input.Trim().Equals(CancelSentinel, StringComparison.OrdinalIgnoreCase);

    private static async Task<JobsiteConfig?> PickOrCreateJobsite(
        JobsiteDirectory jobsites, ForemanDirectory foremen, string repoRoot, string vaultRoot,
        ICliProcessRunner runner, CancellationToken cancellationToken)
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

        string? jobsiteName = null;
        while (jobsiteName is null)
        {
            var input = AnsiConsole.Prompt(
                new TextPrompt<string>("[bold]Jobsite name[/] (e.g. the repo's name; 'cancel' to stop hiring):"));

            if (IsCancel(input))
            {
                return null;
            }

            if (string.IsNullOrWhiteSpace(input))
            {
                AnsiConsole.MarkupLine("[red]Name can't be empty.[/]");
                continue;
            }

            if (jobsites.Find(input) is not null)
            {
                AnsiConsole.MarkupLine($"[red]Jobsite '{Markup.Escape(input)}' already exists.[/]");
                continue;
            }

            jobsiteName = input;
        }

        string? repoPath = null;
        while (repoPath is null)
        {
            var input = AnsiConsole.Prompt(
                new TextPrompt<string>(
                    "[bold]Repo path[/] -- an existing local clone, or a new folder to create it as an empty project ('cancel' to stop hiring):"));

            if (IsCancel(input))
            {
                return null;
            }

            var candidate = input.Trim();
            if (string.IsNullOrEmpty(candidate))
            {
                AnsiConsole.MarkupLine("[red]Path can't be empty.[/]");
                continue;
            }

            if (Directory.Exists(candidate))
            {
                repoPath = candidate;
                break;
            }

            if (!AnsiConsole.Confirm(
                    $"'{Markup.Escape(candidate)}' doesn't exist. Create it as a new, empty project folder?", true))
            {
                continue;
            }

            try
            {
                Directory.CreateDirectory(candidate);
                repoPath = candidate;
            }
            catch (Exception ex)
            {
                AnsiConsole.MarkupLine($"[red]Could not create it:[/] {Markup.Escape(ex.Message)}");
            }
        }

        var repoUrl = AnsiConsole.Prompt(
            new TextPrompt<string>("[bold]Repo URL[/] (optional, blank to skip):").AllowEmpty());

        var defaultBranch = NormalizeBranch(AnsiConsole.Prompt(
            new TextPrompt<string>("[bold]Default branch[/] (blank for [grey]main[/]):").AllowEmpty()));

        // The repo-path step above only ensures the FOLDER exists -- an
        // existing local clone already has a real repo, but a brand-new folder
        // (whether just created above, or an empty one the Boss mkdir'd before
        // hiring) does not. `git init` is safe to run unconditionally here: it
        // is a no-op on a directory that is already a git repo (confirmed via
        // `rev-parse --git-dir` first, rather than assumed), and the Foreman's
        // own later bootstrap step (AI/ConstructionCrew/Templates/foreman-instructions.md's
        // "Stand the repository up" step) checks the same way, so this and
        // that step can never disagree about whether a repo already exists.
        await EnsureGitRepo(repoPath, defaultBranch, runner, cancellationToken);

        var buildCommand = NormalizeOptionalCommand(AnsiConsole.Prompt(
            new TextPrompt<string>("[bold]Build command[/] (blank if you don't know yet):").AllowEmpty()));

        var testCommand = NormalizeOptionalCommand(AnsiConsole.Prompt(
            new TextPrompt<string>("[bold]Test command[/] (blank if you don't know yet):").AllowEmpty()));

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
            DefaultBranch: defaultBranch,
            BuildCommand: buildCommand,
            TestCommand: testCommand,
            // Resolved here, where the Jobsite name is first known, and the
            // answer has to be on the config before AppendJobsite writes it.
            // A recognized layout shows its derived default and takes a yes; a
            // no, or an unrecognized layout, asks outright. Named, not
            // positional -- DefaultBranch/BuildCommand/TestCommand/Upstream sit
            // between ColorName and VaultFolders.
            VaultFolders: ResolveVaultFolders(vaultRoot, jobsiteName));

        var jobsitesYamlPath = Path.Combine(repoRoot, "config", "jobsites.yaml");
        JobsiteConfigWriter.AppendJobsite(jobsitesYamlPath, jobsite, repoRoot);
        jobsites.Add(jobsite);

        return jobsite;
    }

    /// <summary>
    /// Makes <paramref name="repoPath"/> a real git repository if it isn't one
    /// yet. Checked via `rev-parse --git-dir`, the same command the Foreman's
    /// own bootstrap step (AI/ConstructionCrew/Templates/foreman-instructions.md) checks with
    /// -- deliberately the identical check, so hire time and the Foreman's own
    /// later dispatch can never disagree about whether `git init` still needs to
    /// run. Never throws: a repo the Foreman will need to bootstrap for real
    /// (license, README, first commit) is exactly what a bare `git init` here
    /// produces, and a failure here is reported but does not block the hire --
    /// the Boss can always run `git init` by hand afterward.
    /// </summary>
    internal static async Task EnsureGitRepo(
        string repoPath, string defaultBranch, ICliProcessRunner runner, CancellationToken cancellationToken)
    {
        var probe = await runner.RunAsync(
            new CliInvocation("git", ["-C", repoPath, "rev-parse", "--git-dir"], repoPath), cancellationToken);

        if (probe.Succeeded)
        {
            return;
        }

        var init = await runner.RunAsync(
            new CliInvocation("git", ["init", "-b", defaultBranch], repoPath), cancellationToken);

        AnsiConsole.MarkupLine(
            init.Succeeded
                ? $"[green]Initialized an empty git repository[/] at {Markup.Escape(repoPath)} (branch {Markup.Escape(defaultBranch)})."
                : $"[yellow]Could not run 'git init' in {Markup.Escape(repoPath)}:[/] {Markup.Escape(init.StandardError)}");
    }

    private static string Truncate(string text, int max) => text.Length > max ? text[..max] + "..." : text;
}
