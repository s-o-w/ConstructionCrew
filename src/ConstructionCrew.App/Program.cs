using ConstructionCrew.App.Tui;
using ConstructionCrew.Config;
using ConstructionCrew.Core.Abstractions;
using ConstructionCrew.Core.Models;
using ConstructionCrew.Core.Runtime;
using ConstructionCrew.Graph;
using ConstructionCrew.Providers;
using ConstructionCrew.HomeOffice;
using Spectre.Console;

var repoRoot = RepoPaths.FindRepoRoot(AppContext.BaseDirectory);
var settings = AppSettingsLoader.Load(repoRoot, args);

AnsiConsole.Write(new Rule("[bold yellow]ConstructionCrew[/]").LeftJustified());

IReadOnlyList<ForemanConfig> foremenSeed;
try
{
    foremenSeed = new ForemanConfigLoader().LoadFromFile(settings.ForemenConfigPath, repoRoot, settings.VaultRoot, settings.GcForemanName);
}
catch (Exception ex)
{
    AnsiConsole.MarkupLine($"[red]Could not load Foreman config:[/] {Markup.Escape(ex.Message)}");
    return 1;
}

var foremanDirectory = new ForemanDirectory(foremenSeed);
var gcConfig = foremanDirectory.Find(settings.GcForemanName);
if (gcConfig is null)
{
    AnsiConsole.MarkupLine($"[red]No Foreman named '{settings.GcForemanName}' is configured -- that's the reserved name for the GC.[/] Add one to {settings.ForemenConfigPath}.");
    return 1;
}

IReadOnlyList<JobsiteConfig> jobsitesSeed;
try
{
    jobsitesSeed = new JobsiteConfigLoader().LoadFromFile(settings.JobsitesConfigPath, repoRoot);
}
catch (Exception ex)
{
    AnsiConsole.MarkupLine($"[red]Could not load Jobsite config:[/] {Markup.Escape(ex.Message)}");
    return 1;
}

var jobsiteDirectory = new JobsiteDirectory(jobsitesSeed);

// Which CLIs this machine can actually hire: registered in code AND resolvable on
// PATH. There is deliberately no "id != gemini" filter here -- GeminiProvider reports
// IsImplemented == false itself, so it stays out even on a box where `gemini` is
// installed. Results cache to state/tools.json; /settings re-probes.
var providerRegistry = ProviderRegistry.Default(settings.StateDirectory);
var availableProviderIds = providerRegistry.AvailableIds();

var runner = new CliProcessRunner();
// The factory gets every registered provider, not just the available ones, so a
// foremen.yaml naming an uninstalled CLI fails with that CLI's own error rather than
// a misleading "no provider registered for 'codex'".
var agentFactory = new LocalCliAgentFactory(providerRegistry.Registered, runner);
// Exactly one LiveAgentRegistry per process. The Boss loop and JobRegistry both
// route through it, so GC never ends up with two divergent conversations.
var liveAgents = new LiveAgentRegistry(agentFactory);
var statusSink = new JobStatusSink();
var jobRegistry = new JobRegistry(foremanDirectory, agentFactory, statusSink, liveAgents, settings.GcForemanName);
// Program.cs is the one place allowed to construct a cross-project
// implementation and hand HomeOffice an already-built instance -- HomeOffice
// has no ProjectReference to ConstructionCrew.Graph and never names VaultGraph.
var vaultGraph = new VaultGraph();
var vaultOptions = new HomeOfficeVaultOptions(settings.VaultRoot);

Directory.CreateDirectory(settings.StateDirectory);

using var cts = new CancellationTokenSource();
var homeOffice = await HomeOfficeHost.StartAsync(jobRegistry, foremanDirectory, jobsiteDirectory, vaultOptions, vaultGraph, settings.HomeOfficePort, cts.Token);

// --debug is deliberately not surfaced in the TUI itself (the Dashboard footer
// used to always show this and it was just screen clutter) -- it's a one-time
// plain-console line before the TUI takes over, for when a second instance
// (e.g. built from a worktree, on an overridden port) needs to be told apart
// from the live one.
var isDebug = args.Contains("--debug", StringComparer.OrdinalIgnoreCase);
if (isDebug)
{
    AnsiConsole.MarkupLine($"[grey]Home Office listening on {homeOffice.BaseAddress}[/]");
    AnsiConsole.Markup("[grey]Press enter to continue...[/]");
    Console.ReadLine();
}

// Every Foreman needs the Home Office's MCP config to call list_foremen/dispatch_task/
// spawn_worker/ask_foreman -- not just GC, and not just Claude Code. Each available
// provider gets its own config written in its own shape, then everyone hired so far is
// stamped with the wiring for the provider they actually run.
var mcpOptionsByProvider = WriteMcpWiring(providerRegistry, settings.GeneratedConfigDirectory, homeOffice.BaseAddress);

foreach (var foreman in foremanDirectory.All().ToList())
{
    if (!mcpOptionsByProvider.TryGetValue(foreman.Provider, out var mcpOptions))
    {
        continue;
    }

    var merged = new Dictionary<string, string>(foreman.ProviderOptions);
    foreach (var option in mcpOptions)
    {
        merged[option.Key] = option.Value;
    }

    foremanDirectory.Add(foreman with { ProviderOptions = merged });
}

gcConfig = foremanDirectory.Find(settings.GcForemanName)!;

if (!mcpOptionsByProvider.ContainsKey(gcConfig.Provider))
{
    AnsiConsole.MarkupLine($"[yellow]GC's provider '{Markup.Escape(gcConfig.Provider)}' isn't reachable from the Home Office -- it's either not installed or has no verified MCP shape. Run /settings to re-probe.[/]");
}

var state = new DashboardState { HomeOfficeAddress = homeOffice.BaseAddress.ToString() };

while (true)
{
    Dashboard.Render(foremanDirectory, jobsiteDirectory, jobRegistry, state);

    Console.Write("Boss> ");
    var input = Console.ReadLine();

    if (input is null || IsExit(input))
    {
        break;
    }

    if (string.IsNullOrWhiteSpace(input))
    {
        continue;
    }

    var command = input.Trim();

    if (command.Equals("/chat", StringComparison.OrdinalIgnoreCase))
    {
        state.View = TuiView.Chat;
        continue;
    }

    if (command.Equals("/tasks", StringComparison.OrdinalIgnoreCase))
    {
        state.View = TuiView.Tasks;
        continue;
    }

    if (command.Equals("/help", StringComparison.OrdinalIgnoreCase))
    {
        AnsiConsole.MarkupLine("[grey]/chat  /tasks  /hire  /fire  /settings  /exit -- anything else is sent to the GC as a message.[/]");
        AnsiConsole.Markup("[grey]Press enter to continue...[/]");
        Console.ReadLine();
        continue;
    }

    if (command.Equals("/settings", StringComparison.OrdinalIgnoreCase))
    {
        AnsiConsole.Clear();
        AnsiConsole.Write(new Rule("[bold yellow]tool discovery[/]").LeftJustified());

        // Re-probe PATH from scratch (a CLI installed since startup shows up here)
        // and rewrite state/tools.json.
        var probes = providerRegistry.Refresh();
        availableProviderIds = providerRegistry.AvailableIds();
        mcpOptionsByProvider = WriteMcpWiring(providerRegistry, settings.GeneratedConfigDirectory, homeOffice.BaseAddress);

        var table = new Table().Border(TableBorder.Rounded);
        table.AddColumn("provider");
        table.AddColumn("status");
        table.AddColumn("resolved");
        table.AddColumn("home office");

        foreach (var probe in probes)
        {
            var status = !probe.Implemented
                ? "[grey]not implemented[/]"
                : probe.ResolvedPath is null
                    ? "[red]not on PATH[/]"
                    : "[green]available[/]";

            table.AddRow(
                Markup.Escape(probe.ProviderId),
                status,
                Markup.Escape(probe.ResolvedPath ?? probe.ExecutableName),
                mcpOptionsByProvider.ContainsKey(probe.ProviderId) ? "[green]wired[/]" : "[grey]-[/]");
        }

        AnsiConsole.Write(table);
        AnsiConsole.MarkupLine($"[grey]Cached to {Markup.Escape(Path.Combine(settings.StateDirectory, "tools.json"))}.[/]");
        AnsiConsole.Markup("[grey]Press enter to continue...[/]");
        Console.ReadLine();
        state.View = TuiView.Chat;
        continue;
    }

    if (command.Equals("/hire", StringComparison.OrdinalIgnoreCase))
    {
        // Scoped to /hire, not app startup: before Phase 3's FirstRunWizard exists,
        // VaultRoot is only ever set by hand, and a startup-level gate would make
        // the app unusable standalone. Phase 3 makes this a pure backstop.
        if (string.IsNullOrWhiteSpace(settings.VaultRoot) || !Directory.Exists(settings.VaultRoot))
        {
            AnsiConsole.MarkupLine("[yellow]No Vault is configured -- run first-run setup (or set --vault-root) before hiring a Foreman.[/]");
            AnsiConsole.Markup("[grey]Press enter to continue...[/]");
            Console.ReadLine();
            state.View = TuiView.Chat;
            continue;
        }

        AnsiConsole.Clear();
        HireWizard.Run(foremanDirectory, jobsiteDirectory, availableProviderIds, repoRoot, settings.VaultRoot, mcpOptionsByProvider);
        AnsiConsole.Markup("[grey]Press enter to continue...[/]");
        Console.ReadLine();
        state.View = TuiView.Chat;
        continue;
    }

    if (command.Equals("/fire", StringComparison.OrdinalIgnoreCase))
    {
        AnsiConsole.Clear();
        FireWizard.Run(foremanDirectory, jobsiteDirectory, jobRegistry, repoRoot);
        AnsiConsole.Markup("[grey]Press enter to continue...[/]");
        Console.ReadLine();
        state.View = TuiView.Chat;
        continue;
    }

    if (command.StartsWith('/'))
    {
        var stubLabel = command[1..];
        state.View = TuiView.Stub;
        state.StubLabel = stubLabel;
        continue;
    }

    state.View = TuiView.Chat;
    state.Transcript.Add(new TranscriptLine("Boss", input));

    CliRunResult result = null!;
    await AnsiConsole.Status()
        .Spinner(Spinner.Known.Dots)
        .StartAsync("GC is thinking...", async _ =>
        {
            result = await liveAgents.SendAsync(settings.GcForemanName, gcConfig, input, cts.Token);
        });

    state.Transcript.Add(new TranscriptLine("GC", result.Succeeded ? result.StandardOutput : result.StandardError, IsError: !result.Succeeded));
}

await homeOffice.DisposeAsync();
return 0;

// Writes each available provider's Home Office config in that provider's own shape and
// returns the ProviderOptions to stamp onto Foremen running it. A provider with no
// verified MCP shape is warned about, not fatal -- it still works, it just can't call
// Home Office tools.
static Dictionary<string, IReadOnlyDictionary<string, string>> WriteMcpWiring(
    ProviderRegistry registry,
    string generatedConfigDirectory,
    Uri homeOfficeBaseAddress)
{
    var byProvider = new Dictionary<string, IReadOnlyDictionary<string, string>>(StringComparer.OrdinalIgnoreCase);

    foreach (var provider in registry.Available())
    {
        var wiring = McpConfigWriter.Write(provider.ProviderId, generatedConfigDirectory, homeOfficeBaseAddress);
        if (wiring is null)
        {
            AnsiConsole.MarkupLine($"[yellow]No verified Home Office MCP shape for provider '{Markup.Escape(provider.ProviderId)}' -- its Foremen won't be able to call Home Office tools.[/]");
            continue;
        }

        byProvider[provider.ProviderId] = wiring.ProviderOptions;
    }

    return byProvider;
}

static bool IsExit(string input)
{
    var trimmed = input.Trim();
    return trimmed.Equals("exit", StringComparison.OrdinalIgnoreCase) ||
           trimmed.Equals("quit", StringComparison.OrdinalIgnoreCase) ||
           trimmed.Equals("/exit", StringComparison.OrdinalIgnoreCase);
}
