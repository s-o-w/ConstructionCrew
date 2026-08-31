using ConstructionCrew.Config;
using ConstructionCrew.Core.Models;
using ConstructionCrew.Providers;
using Spectre.Console;

namespace ConstructionCrew.App.Tui;

/// <summary>
/// The <c>/foreman [name]</c> command: view a hired Foreman's (or GC's -- GC
/// is just a ForemanConfig with Role GC) details, and edit the fields that are
/// safe to change after hire. Some fields are locked by design, not oversight:
///
/// <list type="bullet">
/// <item>
/// <b>Name</b> is the canonical lookup key everywhere -- <see cref="ForemanDirectory"/>,
/// <c>LiveAgentRegistry</c>, <c>JobRegistry</c>'s workorder-slot map. Changing it
/// after hire would orphan every one of those. GC's Name is additionally
/// load-validated to be exactly the reserved <c>GcForemanName</c>; DisplayName
/// is the "name" that's actually meant to change.
/// </item>
/// <item>
/// <b>Role</b> determines which instructions template was rendered at hire time
/// and is load-validated (exactly one GC, named GcForemanName) -- changing it
/// in place would desync from both.
/// </item>
/// <item>
/// <b>WorkingDirectory</b> is foundational to where the role actually operates (a
/// Jobsite repo clone for a Foreman, the Vault for GC). GC's is managed by
/// <c>/settings</c>' Vault setup flow specifically, not this generic editor.
/// </item>
/// <item>
/// <b>InstructionsFilePath</b> is tied to the template rendered at hire time --
/// repointing it here would silently desync what the role was told from
/// what's on disk.
/// </item>
/// </list>
///
/// A Foreman tied to a Jobsite (one Foreman per Jobsite, by design) also shows
/// and can edit that Jobsite's own fields -- Description, RepoUrl, etc.
/// <b>RepoPath</b> is excluded there for the same "foundational, not casually
/// editable" reason WorkingDirectory is excluded above.
/// </summary>
public static class ForemanDetailsCommand
{
    private const string Verb = "/foreman";

    /// <summary>
    /// Parses <c>/foreman Frontend</c>, matching <c>DriveCommands.TryParseDrive</c>'s
    /// exact shape. <paramref name="target"/> comes back empty for a bare
    /// <c>/foreman</c> (prompts a picker); <c>/foremanx</c> is not this command at all.
    /// </summary>
    internal static bool TryParse(string command, out string target)
    {
        target = string.Empty;

        var trimmed = command.Trim();
        if (!trimmed.StartsWith(Verb, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var rest = trimmed[Verb.Length..];
        if (rest.Length > 0 && !char.IsWhiteSpace(rest[0]))
        {
            return false;
        }

        target = rest.Trim();
        return true;
    }

    private static readonly string[] EditableForemanFields =
        ["display name", "provider", "jobsite", "add dirs", "vault folders", "provider options"];

    private static readonly string[] EditableJobsiteFields =
        ["description", "repo url", "color", "default branch", "build command", "test command", "upstream", "vault folders"];

    public static void Run(
        ForemanDirectory foremen,
        JobsiteDirectory jobsites,
        string foremenConfigPath,
        string jobsitesConfigPath,
        string repoRoot,
        string? vaultRoot,
        IReadOnlyList<string> availableProviderIds,
        string argument)
    {
        var foreman = ResolveTarget(foremen, argument);
        if (foreman is null)
        {
            return;
        }

        while (true)
        {
            AnsiConsole.Clear();
            AnsiConsole.Write(new Rule($"[bold yellow]{Markup.Escape(foreman.DisplayName ?? foreman.Name)}[/]").LeftJustified());

            var jobsite = foreman.JobsiteName is not null ? jobsites.Find(foreman.JobsiteName) : null;

            AnsiConsole.Write(BuildForemanTable(foreman));

            if (jobsite is not null)
            {
                AnsiConsole.WriteLine();
                AnsiConsole.MarkupLine($"[bold]Jobsite:[/] {Markup.Escape(jobsite.Name)}");
                AnsiConsole.Write(BuildJobsiteTable(jobsite));
            }
            else if (foreman.JobsiteName is not null)
            {
                AnsiConsole.MarkupLine($"[yellow]Jobsite '{Markup.Escape(foreman.JobsiteName)}' is assigned but not found in jobsites.yaml.[/]");
            }

            AnsiConsole.WriteLine();

            var choices = new List<string>(EditableForemanFields);
            if (jobsite is not null)
            {
                choices.AddRange(EditableJobsiteFields.Select(f => $"jobsite: {f}"));
            }

            choices.Add("done");

            var choice = AnsiConsole.Prompt(
                new SelectionPrompt<string>().Title("[bold]Edit which field?[/] (or done)").AddChoices(choices));

            if (choice == "done")
            {
                return;
            }

            if (choice.StartsWith("jobsite: ", StringComparison.Ordinal) && jobsite is not null)
            {
                var updatedJobsite = EditJobsiteField(jobsite, choice["jobsite: ".Length..]);
                JobsiteConfigWriter.RemoveJobsite(jobsitesConfigPath, jobsite.Name);
                JobsiteConfigWriter.AppendJobsite(jobsitesConfigPath, updatedJobsite, repoRoot);
                jobsites.Add(updatedJobsite);
            }
            else
            {
                var updated = EditForemanField(foreman, choice, foremen, jobsites, availableProviderIds);
                ForemanConfigWriter.RemoveForeman(foremenConfigPath, foreman.Name);
                ForemanConfigWriter.AppendForeman(foremenConfigPath, updated, repoRoot, vaultRoot);
                foremen.Add(updated);
                foreman = updated;
            }
        }
    }

    private static ForemanConfig? ResolveTarget(ForemanDirectory foremen, string argument)
    {
        if (!string.IsNullOrWhiteSpace(argument))
        {
            var found = foremen.Find(argument.Trim());
            if (found is null)
            {
                AnsiConsole.MarkupLine($"[red]No Foreman named '{Markup.Escape(argument.Trim())}' is hired.[/]");
                AnsiConsole.Markup("[grey]Press enter to continue...[/]");
                Console.ReadLine();
            }

            return found;
        }

        var all = foremen.All().OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase).ToList();
        if (all.Count == 0)
        {
            AnsiConsole.MarkupLine("[yellow]Nobody is hired yet.[/]");
            AnsiConsole.Markup("[grey]Press enter to continue...[/]");
            Console.ReadLine();
            return null;
        }

        var picked = AnsiConsole.Prompt(
            new SelectionPrompt<ForemanConfig>()
                .Title("[bold]Which Foreman?[/]")
                .UseConverter(f => f.DisplayName is null ? f.Name : $"{f.Name} ({f.DisplayName})")
                .AddChoices(all));

        return picked;
    }

    private static Table BuildForemanTable(ForemanConfig foreman)
    {
        var table = new Table().Border(TableBorder.Rounded);
        table.AddColumn("field");
        table.AddColumn("value");
        table.AddColumn("");

        table.AddRow("name", Markup.Escape(foreman.Name), "[grey]fixed[/]");
        table.AddRow("role", foreman.Role.ToString(), "[grey]fixed[/]");
        table.AddRow("display name", Markup.Escape(foreman.DisplayName ?? "-"), "[green]editable[/]");
        table.AddRow("provider", Markup.Escape(foreman.Provider), "[green]editable[/]");
        table.AddRow("working directory", Markup.Escape(foreman.WorkingDirectory), "[grey]fixed[/]");
        table.AddRow("instructions file", Markup.Escape(foreman.InstructionsFilePath), "[grey]fixed[/]");
        table.AddRow("jobsite", Markup.Escape(foreman.JobsiteName ?? "-"), "[green]editable[/]");
        table.AddRow("add dirs", Markup.Escape(FormatList(foreman.AddDirs)), "[green]editable[/]");
        table.AddRow("vault folders", Markup.Escape(FormatList(foreman.VaultFolders)), "[green]editable[/]");
        table.AddRow("provider options", Markup.Escape(FormatDict(foreman.ProviderOptions)), "[green]editable[/]");

        return table;
    }

    private static Table BuildJobsiteTable(JobsiteConfig jobsite)
    {
        var table = new Table().Border(TableBorder.Rounded);
        table.AddColumn("field");
        table.AddColumn("value");
        table.AddColumn("");

        table.AddRow("repo path", Markup.Escape(jobsite.RepoPath), "[grey]fixed[/]");
        table.AddRow("description", Markup.Escape(jobsite.Description), "[green]editable[/]");
        table.AddRow("repo url", Markup.Escape(jobsite.RepoUrl ?? "-"), "[green]editable[/]");
        table.AddRow("color", Markup.Escape(jobsite.ColorName ?? "-"), "[green]editable[/]");
        table.AddRow("default branch", Markup.Escape(jobsite.DefaultBranch ?? "-"), "[green]editable[/]");
        table.AddRow("build command", Markup.Escape(jobsite.BuildCommand ?? "-"), "[green]editable[/]");
        table.AddRow("test command", Markup.Escape(jobsite.TestCommand ?? "-"), "[green]editable[/]");
        table.AddRow("upstream", Markup.Escape(FormatDict(jobsite.Upstream)), "[green]editable[/]");
        table.AddRow("vault folders", Markup.Escape(FormatList(jobsite.VaultFolders)), "[green]editable[/]");

        return table;
    }

    private static ForemanConfig EditForemanField(
        ForemanConfig foreman,
        string field,
        ForemanDirectory foremen,
        JobsiteDirectory jobsites,
        IReadOnlyList<string> availableProviderIds)
    {
        switch (field)
        {
            case "display name":
                var displayName = AnsiConsole.Prompt(
                    new TextPrompt<string>("[bold]Display name[/] (blank to clear):")
                        .DefaultValue(foreman.DisplayName ?? string.Empty)
                        .AllowEmpty());
                return foreman with { DisplayName = string.IsNullOrWhiteSpace(displayName) ? null : displayName.Trim() };

            case "provider":
                if (availableProviderIds.Count == 0)
                {
                    AnsiConsole.MarkupLine("[yellow]No providers are available to switch to.[/]");
                    return foreman;
                }

                var provider = AnsiConsole.Prompt(
                    new SelectionPrompt<string>().Title("[bold]Provider[/]").AddChoices(availableProviderIds));

                return foreman with { Provider = provider, ProviderOptions = ToolPolicyForSwitch(foreman.Role, provider) };

            case "jobsite":
                return ReassignJobsite(foreman, foremen, jobsites);

            case "add dirs":
                var addDirsInput = AnsiConsole.Prompt(
                    new TextPrompt<string>("[bold]Add dirs[/], comma-separated (blank to clear):")
                        .DefaultValue(FormatList(foreman.AddDirs))
                        .AllowEmpty());
                return foreman with { AddDirs = ParseList(addDirsInput) };

            case "vault folders":
                var vaultFoldersInput = AnsiConsole.Prompt(
                    new TextPrompt<string>("[bold]Vault folders[/], comma-separated (blank to clear):")
                        .DefaultValue(FormatList(foreman.VaultFolders))
                        .AllowEmpty());
                return foreman with { VaultFolders = ParseList(vaultFoldersInput) };

            case "provider options":
                var optionsInput = AnsiConsole.Prompt(
                    new TextPrompt<string>("[bold]Provider options[/], comma-separated key=value (blank to clear):")
                        .DefaultValue(FormatDict(foreman.ProviderOptions))
                        .AllowEmpty());
                return foreman with { ProviderOptions = ParseDict(optionsInput) ?? new Dictionary<string, string>() };

            default:
                return foreman;
        }
    }

    /// <summary>
    /// Each CLI has its own permission vocabulary (ProviderDefaults' own doc
    /// comment) -- carrying the old provider's ProviderOptions over on a switch
    /// would silently grant the new one nothing (Claude's allowedTools means
    /// nothing to Codex's sandbox policy, and vice versa). Reset to the new
    /// provider's own working default instead of leaving stale keys behind; any
    /// hand-tuning done via "provider options" is lost on a provider switch,
    /// same as it would be re-hiring under the new provider.
    /// </summary>
    internal static IReadOnlyDictionary<string, string> ToolPolicyForSwitch(CrewRole role, string provider) =>
        role == CrewRole.GC ? ProviderDefaults.GcToolPolicy(provider) : ProviderDefaults.ToolPolicy(provider);

    /// <summary>
    /// One Foreman per Jobsite, by design (JobsiteConfig's own doc comment). A
    /// Jobsite already claimed by a different Foreman is refused rather than
    /// silently double-assigned -- the Boss picks a different Jobsite, or frees
    /// the current one first via that other Foreman's own "(none)" choice here.
    /// </summary>
    private static ForemanConfig ReassignJobsite(ForemanConfig foreman, ForemanDirectory foremen, JobsiteDirectory jobsites)
    {
        var jobsiteNames = jobsites.All().Select(j => j.Name).OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();
        jobsiteNames.Insert(0, "(none)");

        var chosen = AnsiConsole.Prompt(new SelectionPrompt<string>().Title("[bold]Jobsite[/]").AddChoices(jobsiteNames));

        if (chosen == "(none)")
        {
            return foreman with { JobsiteName = null };
        }

        var currentHolder = foremen.All().FirstOrDefault(f =>
            !f.Name.Equals(foreman.Name, StringComparison.OrdinalIgnoreCase) &&
            f.JobsiteName is not null &&
            f.JobsiteName.Equals(chosen, StringComparison.OrdinalIgnoreCase));

        if (currentHolder is not null)
        {
            AnsiConsole.MarkupLine(
                $"[red]'{Markup.Escape(chosen)}' is already assigned to {Markup.Escape(currentHolder.Name)}[/] -- " +
                "one Foreman per Jobsite. Unassign it there first.");
            AnsiConsole.Markup("[grey]Press enter to continue...[/]");
            Console.ReadLine();
            return foreman;
        }

        return foreman with { JobsiteName = chosen };
    }

    private static JobsiteConfig EditJobsiteField(JobsiteConfig jobsite, string field)
    {
        switch (field)
        {
            case "description":
                var description = AnsiConsole.Prompt(
                    new TextPrompt<string>("[bold]Description[/]:")
                        .DefaultValue(jobsite.Description)
                        .Validate(d => string.IsNullOrWhiteSpace(d) ? ValidationResult.Error("Can't be empty.") : ValidationResult.Success()));
                return jobsite with { Description = description };

            case "repo url":
                var repoUrl = AnsiConsole.Prompt(
                    new TextPrompt<string>("[bold]Repo URL[/] (blank to clear):").DefaultValue(jobsite.RepoUrl ?? string.Empty).AllowEmpty());
                return jobsite with { RepoUrl = string.IsNullOrWhiteSpace(repoUrl) ? null : repoUrl.Trim() };

            case "color":
                var color = AnsiConsole.Prompt(
                    new TextPrompt<string>("[bold]Color[/] (blank to clear):").DefaultValue(jobsite.ColorName ?? string.Empty).AllowEmpty());
                return jobsite with { ColorName = string.IsNullOrWhiteSpace(color) ? null : color.Trim() };

            case "default branch":
                var defaultBranch = AnsiConsole.Prompt(
                    new TextPrompt<string>("[bold]Default branch[/] (blank to clear):").DefaultValue(jobsite.DefaultBranch ?? string.Empty).AllowEmpty());
                return jobsite with { DefaultBranch = string.IsNullOrWhiteSpace(defaultBranch) ? null : defaultBranch.Trim() };

            case "build command":
                var buildCommand = AnsiConsole.Prompt(
                    new TextPrompt<string>("[bold]Build command[/] (blank to clear):").DefaultValue(jobsite.BuildCommand ?? string.Empty).AllowEmpty());
                return jobsite with { BuildCommand = string.IsNullOrWhiteSpace(buildCommand) ? null : buildCommand.Trim() };

            case "test command":
                var testCommand = AnsiConsole.Prompt(
                    new TextPrompt<string>("[bold]Test command[/] (blank to clear):").DefaultValue(jobsite.TestCommand ?? string.Empty).AllowEmpty());
                return jobsite with { TestCommand = string.IsNullOrWhiteSpace(testCommand) ? null : testCommand.Trim() };

            case "upstream":
                var upstreamInput = AnsiConsole.Prompt(
                    new TextPrompt<string>("[bold]Upstream[/], comma-separated key=value (blank to clear):")
                        .DefaultValue(FormatDict(jobsite.Upstream))
                        .AllowEmpty());
                return jobsite with { Upstream = ParseDict(upstreamInput) };

            case "vault folders":
                var vaultFoldersInput = AnsiConsole.Prompt(
                    new TextPrompt<string>("[bold]Vault folders[/], comma-separated (blank to clear):")
                        .DefaultValue(FormatList(jobsite.VaultFolders))
                        .AllowEmpty());
                return jobsite with { VaultFolders = ParseList(vaultFoldersInput) };

            default:
                return jobsite;
        }
    }

    /// <summary>Null/blank collapses to null (the field's own "unset" state) -- never an empty, allocated list.</summary>
    internal static IReadOnlyList<string>? ParseList(string? input)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            return null;
        }

        var items = input.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        return items.Length == 0 ? null : items;
    }

    internal static string FormatList(IReadOnlyList<string>? list) => list is null or { Count: 0 } ? string.Empty : string.Join(", ", list);

    /// <summary>
    /// "key=value, key2=value2" -&gt; a dictionary. Null/blank collapses to null. An
    /// entry with no "=" is skipped rather than thrown on -- a typo here should
    /// cost one entry, not the whole edit.
    /// </summary>
    internal static IReadOnlyDictionary<string, string>? ParseDict(string? input)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            return null;
        }

        var result = new Dictionary<string, string>();
        foreach (var pair in input.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            var splitAt = pair.IndexOf('=');
            if (splitAt <= 0)
            {
                continue;
            }

            result[pair[..splitAt].Trim()] = pair[(splitAt + 1)..].Trim();
        }

        return result.Count == 0 ? null : result;
    }

    internal static string FormatDict(IReadOnlyDictionary<string, string>? dict) =>
        dict is null || dict.Count == 0 ? string.Empty : string.Join(", ", dict.Select(kv => $"{kv.Key}={kv.Value}"));
}
