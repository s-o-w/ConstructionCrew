using System.Threading.Channels;
using ConstructionCrew.App;
using ConstructionCrew.App.Tui;
using ConstructionCrew.Config;
using ConstructionCrew.Core.Abstractions;
using ConstructionCrew.Core.Models;
using ConstructionCrew.Core.Runtime;
using ConstructionCrew.Git;
using ConstructionCrew.Graph;
using ConstructionCrew.Providers;
using ConstructionCrew.HomeOffice;
using Spectre.Console;

var repoRoot = RepoPaths.FindRepoRoot(AppContext.BaseDirectory);
var settings = AppSettingsLoader.Load(repoRoot, args);

AnsiConsole.Write(new Rule("[bold yellow]ConstructionCrew[/]").LeftJustified());

// Which CLIs this machine can actually hire: registered in code AND resolvable on
// PATH. There is deliberately no "id != gemini" filter here -- GeminiProvider reports
// IsImplemented == false itself, so it stays out even on a box where `gemini` is
// installed. Results cache to state/tools.json; /settings re-probes.
//
// Resolved before the roster loads because first run needs the list to hire a GC with.
var providerRegistry = ProviderRegistry.Default(settings.StateDirectory);
var availableProviderIds = providerRegistry.AvailableIds();

// No roster file at all is a fresh install, not a broken config -- run first-run
// setup instead of failing. This is deliberately NOT the "gcConfig is null" branch
// below: that one means a file that loads but has no GC-named entry, which is a
// genuinely different failure and stays a hard fail.
if (!File.Exists(settings.ForemenConfigPath))
{
    string? resolvedVaultRoot;
    try
    {
        resolvedVaultRoot = FirstRunWizard.Run(repoRoot, settings, availableProviderIds);
    }
    catch (Exception ex)
    {
        // First run is the one wizard that runs before the TUI is up, so it is
        // also the one that can be hit with no terminal at all (a piped run, CI).
        // Spectre throws rather than degrades there; report it like every other
        // startup failure instead of dumping a stack trace.
        AnsiConsole.MarkupLine($"[red]First-run setup could not run:[/] {Markup.Escape(ex.Message)}");
        AnsiConsole.MarkupLine("[grey]It needs an interactive terminal. Copy config/foremen.yaml.example to config/foremen.yaml to set the roster up by hand instead.[/]");
        return 1;
    }

    if (resolvedVaultRoot is null)
    {
        AnsiConsole.MarkupLine("[yellow]First-run setup didn't finish -- nothing to run without a GC.[/]");
        return 1;
    }

    // AppSettings is a record and `settings` is a plain local: nothing reloads it
    // from disk. Without this reassignment the ${vaultRoot} the wizard just wrote
    // would fail to expand on the very next line, and /hire's Vault guard would
    // still see null for the rest of this process.
    settings = settings with { VaultRoot = resolvedVaultRoot };
}

// Both instructions templates tell every crew member to read the Boss's crew
// preferences file, unconditionally. Scaffolding alone doesn't cover that: it only
// runs when the Boss chose "scaffold a new Vault", and most Bosses point at an
// existing one. So ensure the single file on every start, for whatever vault is
// configured now (including one the first-run wizard just resolved).
//
// Never fatal. A read-only vault, a permissions problem, a vault on a disconnected
// share -- none of that is a reason to refuse to start. The crew reading a missing
// preferences file is a correct outcome; not starting is not.
if (!string.IsNullOrWhiteSpace(settings.VaultRoot) && Directory.Exists(settings.VaultRoot))
{
    try
    {
        VaultLayout.EnsureScaffoldFile(
            VaultLayout.ScaffoldSourceDirectory(repoRoot),
            settings.VaultRoot,
            VaultLayout.CrewPreferencesRelativePath);
    }
    catch (Exception ex)
    {
        AnsiConsole.MarkupLine(
            $"[yellow]Could not create {VaultLayout.CrewPreferencesRelativePath} in the Vault:[/] {Markup.Escape(ex.Message)}");
    }
}

// GC.md is no longer shipped (Phase 4) -- it's rendered fresh, per install, the
// first time it's needed. First run's own call to EnsureGcInstructions only
// fires when foremen.yaml doesn't exist yet; an EXISTING roster (the far more
// common case) already names this file's conventional path, so first run never
// runs again to regenerate it. Ensure it here too, unconditionally, right before
// the loader -- which hard-fails on a missing instructionsFilePath -- ever gets
// a chance to see it missing.
if (!string.IsNullOrWhiteSpace(settings.VaultRoot) && Directory.Exists(settings.VaultRoot))
{
    try
    {
        FirstRunWizard.EnsureGcInstructions(settings.VaultRoot, settings.GcForemanName, availableProviderIds);
    }
    catch (Exception ex)
    {
        AnsiConsole.MarkupLine($"[yellow]Could not render GC's instructions file:[/] {Markup.Escape(ex.Message)}");
    }
}

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

var runner = new CliProcessRunner();
// The factory gets every registered provider, not just the available ones, so a
// foremen.yaml naming an uninstalled CLI fails with that CLI's own error rather than
// a misleading "no provider registered for 'codex'".
var agentFactory = new LocalCliAgentFactory(providerRegistry.Registered, runner);
// Built before JobRegistry, not just before HomeOfficeHost.StartAsync: JobRegistry
// needs this same instance for spawn_worker's worktree-per-Worker mechanism, and
// HomeOfficeHost registers the very same one for the three worktree tools. One
// instance, two consumers, reusing `runner` above.
var worktreeManager = new WorktreeManager(runner);
// Exactly one LiveAgentRegistry per process. The Boss loop and JobRegistry both
// route through it, so GC never ends up with two divergent conversations.
var liveAgents = new LiveAgentRegistry(agentFactory);
var statusSink = new JobStatusSink();
var runtimeOptions = new JobRegistryRuntimeOptions(settings.StateDirectory);
// The Boss's optional external-notification hook, and the two log writers -- all
// three live outside HomeOffice's reference graph (or, for the options record,
// have JobRegistry as their only consumer), so Program.cs constructs them and
// hands JobRegistry the instances. No HomeOfficeHost registration for any of them.
var notificationOptions = new HomeOfficeNotificationOptions(settings.NotificationsCommand);
var runLogWriter = new RunLogWriter();
// After runtimeOptions, deliberately: it reads StateDirectory, and its
// constructor creates that directory so the very first append cannot throw.
var jobsLogWriter = new JobsLogWriter(Path.Combine(runtimeOptions.StateDirectory, "jobs.jsonl"));
var jobRegistry = new JobRegistry(
    foremanDirectory,
    jobsiteDirectory,
    agentFactory,
    statusSink,
    liveAgents,
    settings.GcForemanName,
    worktreeManager,
    runtimeOptions,
    runner,
    notificationOptions,
    runLogWriter,
    jobsLogWriter);
// Program.cs is the one place allowed to construct a cross-project
// implementation and hand HomeOffice an already-built instance -- HomeOffice
// has no ProjectReference to ConstructionCrew.Graph and never names VaultGraph.
var vaultGraph = new VaultGraph();
var vaultOptions = new HomeOfficeVaultOptions(settings.VaultRoot);
// Same rule: WorkorderReader lives in Config, which HomeOffice does not
// reference. Program.cs news it; HomeOfficeHost registers the instance.
var workorderReader = new WorkorderReader();
// Same rule again: SitrepWriter lives in Config, and file_sitrep only ever sees
// the ISitrepWriter interface.
var sitrepWriter = new SitrepWriter();

Directory.CreateDirectory(settings.StateDirectory);

using var cts = new CancellationTokenSource();
var homeOffice = await HomeOfficeHost.StartAsync(jobRegistry, foremanDirectory, jobsiteDirectory, vaultOptions, workorderReader, worktreeManager, sitrepWriter, vaultGraph, settings.HomeOfficePort, cts.Token);

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

// A local function, not a straight-line loop, because /settings re-probes and
// reassigns mcpOptionsByProvider: without re-stamping, the roster keeps pointing at
// the config paths written before the re-probe. The capture is by reference (C#
// closes over the VARIABLE, not a snapshot of its value), so the /settings call
// below reads the freshly assigned dictionary, not the startup one.
void StampMcpWiring()
{
    foreach (var foreman in foremanDirectory.All().ToList())
    {
        var updated = foreman;

        if (mcpOptionsByProvider.TryGetValue(foreman.Provider, out var mcpOptions))
        {
            var merged = new Dictionary<string, string>(foreman.ProviderOptions);
            foreach (var option in mcpOptions)
            {
                merged[option.Key] = option.Value;
            }

            updated = updated with { ProviderOptions = merged };
        }
        else if (foreman.Role != CrewRole.GC)
        {
            continue;
        }

        // GC only: a roster that already exists (hand-written, or copied from an older
        // foremen.yaml.example) never picks up a ProviderDefaults change, because
        // GcToolPolicy is consulted at first-run hire and nowhere else. That is the
        // actual cause of "GC stopped to ask for interactive approval on a Home Office
        // tool": under `claude -p` a tool outside --allowedTools is auto-denied. Repair
        // it here, and persist the repair so the next start is already correct.
        if (foreman.Role == CrewRole.GC)
        {
            var repairs = new List<string>();

            var policed = ProviderDefaults.EnsureGcToolPolicy(foreman.Provider, updated.ProviderOptions);
            if (!ReferenceEquals(policed, updated.ProviderOptions))
            {
                updated = updated with { ProviderOptions = new Dictionary<string, string>(policed) };
                repairs.Add("tool policy");
            }

            if (updated.VaultFolders is null or { Count: 0 })
            {
                updated = updated with { VaultFolders = FirstRunWizard.GcVaultFolders };
                repairs.Add("vault write scope");
            }

            if (repairs.Count > 0)
            {
                ForemanConfigWriter.RemoveForeman(settings.ForemenConfigPath, foreman.Name);
                ForemanConfigWriter.AppendForeman(settings.ForemenConfigPath, updated, repoRoot, settings.VaultRoot);

                AnsiConsole.MarkupLine(
                    $"[grey]Repaired {Markup.Escape(foreman.Name)}'s config in " +
                    $"{Markup.Escape(settings.ForemenConfigPath)}: {Markup.Escape(string.Join(", ", repairs))}.[/]");
            }
        }

        if (!ReferenceEquals(updated, foreman))
        {
            foremanDirectory.Add(updated);
        }
    }
}

StampMcpWiring();

gcConfig = foremanDirectory.Find(settings.GcForemanName)!;

if (!mcpOptionsByProvider.ContainsKey(gcConfig.Provider))
{
    AnsiConsole.MarkupLine($"[yellow]GC's provider '{Markup.Escape(gcConfig.Provider)}' isn't reachable from the Home Office -- it's either not installed or has no verified MCP shape. Run /settings to re-probe.[/]");
}

var state = new DashboardState
{
    HomeOfficeAddress = homeOffice.BaseAddress.ToString(),
    GcForemanName = settings.GcForemanName,
};

// Boss turns dispatched but not yet reported back. See PendingBossTurns: this is
// the whole completion-notice mechanism, and it exists so JobRegistry does not
// have to grow a public completion callback for the TUI's benefit.
var pendingBossTurns = new PendingBossTurns();

// Read-only git for the passive column, on the same ICliProcessRunner
// WorktreeManager already shells through -- no second process seam.
var gitInspector = new GitWorkspaceInspector(runner);

// One event channel, three producers (the input pump, the IJobStatusSink pump,
// and passive refreshes), exactly one consumer: this loop. Single-reader is what
// lets every DashboardState mutation happen on one thread with no locking.
var events = Channel.CreateUnbounded<BossEvent>(new UnboundedChannelOptions { SingleReader = true });

var bossInput = new BossInputReader(Console.ReadLine);
bossInput.Start();

_ = PumpInputAsync(bossInput.Reader, events.Writer, cts.Token);
_ = PumpJobStatusAsync(statusSink.Reader, events.Writer, cts.Token);

// 0 = no passive refresh in flight. Interlocked, because the refresh completes on
// a thread pool thread while the loop may already be deciding to start another.
var passiveRefreshInFlight = 0;

// The input thread reads nothing until the loop asks for a line. Starts true so
// the first render is followed immediately by the first prompt.
var wantsInput = true;
var running = true;

while (running)
{
    Dashboard.Render(foremanDirectory, jobsiteDirectory, jobRegistry, state);
    Console.Write(state.DrivenForeman is null ? "Boss> " : $"Boss[{state.DrivenForeman}]> ");

    // Granted only after the render, and only once per line consumed: while a
    // modal wizard owns the console, the input thread is parked on this gate
    // rather than racing the wizard's own prompts for stdin.
    if (wantsInput)
    {
        wantsInput = false;
        bossInput.Resume();
    }

    BossEvent first;
    try
    {
        first = await events.Reader.ReadAsync(cts.Token);
    }
    catch (OperationCanceledException)
    {
        break;
    }
    catch (ChannelClosedException)
    {
        break;
    }

    // Drain whatever else is already queued and render once for the lot -- a
    // burst of transitions from several Workers is one redraw, not five.
    var batch = new List<BossEvent> { first };
    while (events.Reader.TryRead(out var next))
    {
        batch.Add(next);
    }

    var refreshPassive = false;

    foreach (var bossEvent in batch)
    {
        switch (bossEvent)
        {
            case BossEvent.JobTransition transition:
                // Every drained record is checked against the pending set. A
                // tracked id that has gone terminal becomes a transcript line in
                // whichever conversation the Boss addressed.
                if (pendingBossTurns.TryTakeCompletion(transition.Record, out var speaker, out var completion))
                {
                    state.TranscriptFor(speaker).Add(completion);
                }

                refreshPassive = true;
                break;

            case BossEvent.InputClosed:
                running = false;
                break;

            case BossEvent.PassiveRefreshed refreshed:
                state.Passive = refreshed.Snapshot;
                break;

            case BossEvent.InputLine line:
                var drivenBefore = state.DrivenForeman;
                try
                {
                    running = await HandleBossLine(line.Text);
                }
                catch (Exception ex)
                {
                    // The loop is now the only thing keeping the Home Office up and
                    // background jobs running: one bad command must not take those
                    // with it. Report it in the transcript and carry on.
                    state.ActiveTranscript.Add(new TranscriptLine("home office", ex.Message, IsError: true));
                }
                finally
                {
                    // Always, even if handling threw: skipping this parks the
                    // input thread forever and the app looks hung.
                    wantsInput = true;
                }

                if (!string.Equals(drivenBefore, state.DrivenForeman, StringComparison.OrdinalIgnoreCase))
                {
                    refreshPassive = true;
                }

                break;
        }

        if (!running)
        {
            break;
        }
    }

    // Deliberately not triggered by PassiveRefreshed itself -- that would be a
    // refresh loop that never idles.
    if (running && refreshPassive && state.DrivenForeman is not null &&
        Interlocked.CompareExchange(ref passiveRefreshInFlight, 1, 0) == 0)
    {
        _ = RefreshPassiveAsync(ResolveWorktreePath(jobRegistry, state.DrivenForeman));
    }
}

bossInput.Dispose();
cts.Cancel();
await homeOffice.DisposeAsync();
return 0;

// Returns false when the Boss asked to leave. Never awaits an agent turn: a
// dispatch is JobRegistry.StartJob, which hands back a job id and runs the turn
// in the background, so the Boss can keep typing while GC works.
async Task<bool> HandleBossLine(string input)
{
    if (string.IsNullOrWhiteSpace(input))
    {
        return true;
    }

    var command = input.Trim();

    // Drive routing gets first look: /exit means "leave this Foreman" while
    // driving and "quit" otherwise, and /drive must not fall through to the
    // unknown-slash-command stub below.
    switch (DriveCommands.Apply(state, command, foremanDirectory.Find, jobRegistry.GetAllJobs()))
    {
        case BossCommandResult.Quit:
            return false;
        case BossCommandResult.Handled:
            return true;
    }

    if (command.Equals("/chat", StringComparison.OrdinalIgnoreCase))
    {
        state.View = TuiView.Chat;
        return true;
    }

    if (command.Equals("/tasks", StringComparison.OrdinalIgnoreCase))
    {
        state.View = TuiView.Tasks;
        return true;
    }

    if (command.Equals("/monitor", StringComparison.OrdinalIgnoreCase))
    {
        state.View = TuiView.Monitor;
        return true;
    }

    // The tab is set first and cleared last, so the strip shows "memory" for as
    // long as the modal browser owns the screen. The browser itself never leaves
    // the crew's own vault folders -- see MemoryBrowser.Roots.
    if (command.Equals("/memory", StringComparison.OrdinalIgnoreCase))
    {
        state.View = TuiView.Memory;
        AnsiConsole.Clear();
        MemoryBrowser.Run(settings.VaultRoot, foremanDirectory, repoRoot);
        state.View = TuiView.Chat;
        return true;
    }

    if (command.Equals("/help", StringComparison.OrdinalIgnoreCase))
    {
        AnsiConsole.MarkupLine("[grey]/chat  /tasks  /monitor  /memory  /hire  /fire  /foreman <Name>  /view <path>  /drive <Foreman>  /settings  /exit -- anything else is sent to the GC (or the driven Foreman) as a message.[/]");
        AnsiConsole.Markup("[grey]Press enter to continue...[/]");
        Console.ReadLine();
        return true;
    }

    if (command.Equals("/settings", StringComparison.OrdinalIgnoreCase))
    {
        AnsiConsole.Clear();
        AnsiConsole.Write(new Rule("[bold yellow]settings[/]").LeftJustified());

        // No inline setup offer when a Vault is already configured -- reconfiguring
        // isn't this command's job, only getting an unconfigured Boss unstuck is.
        if (string.IsNullOrWhiteSpace(settings.VaultRoot) || !Directory.Exists(settings.VaultRoot))
        {
            AnsiConsole.MarkupLine("[bold]Vault:[/] [yellow]not configured[/]");

            if (AnsiConsole.Confirm("Configure a Vault now?", true))
            {
                var resolvedVaultRoot = FirstRunWizard.SetupVaultOnly(
                    repoRoot, foremanDirectory, settings.ForemenConfigPath, settings.GcForemanName);

                if (resolvedVaultRoot is not null)
                {
                    settings = settings with { VaultRoot = resolvedVaultRoot };
                    AnsiConsole.MarkupLine($"[bold green]Vault configured:[/] {Markup.Escape(resolvedVaultRoot)}");
                }
                else
                {
                    AnsiConsole.MarkupLine("[yellow]Vault setup cancelled -- nothing changed.[/]");
                }
            }
        }
        else
        {
            AnsiConsole.MarkupLine($"[bold]Vault:[/] [green]{Markup.Escape(settings.VaultRoot)}[/]");
        }

        AnsiConsole.WriteLine();
        AnsiConsole.Write(new Rule("[bold yellow]tool discovery[/]").LeftJustified());

        // Re-probe PATH from scratch (a CLI installed since startup shows up here)
        // and rewrite state/tools.json.
        var probes = providerRegistry.Refresh();
        availableProviderIds = providerRegistry.AvailableIds();
        mcpOptionsByProvider = WriteMcpWiring(providerRegistry, settings.GeneratedConfigDirectory, homeOffice.BaseAddress);

        // The re-probe just rewrote the MCP configs (and may have wired a provider that
        // was not installed at startup), so the roster's stamped paths are stale until
        // this runs. It re-heals GC's tool policy on the way past, same as at startup.
        StampMcpWiring();

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
        return true;
    }

    if (ForemanDetailsCommand.TryParse(command, out var foremanDetailsTarget))
    {
        AnsiConsole.Clear();
        ForemanDetailsCommand.Run(
            foremanDirectory, jobsiteDirectory, jobRegistry, settings.ForemenConfigPath, settings.JobsitesConfigPath,
            repoRoot, settings.VaultRoot, availableProviderIds, mcpOptionsByProvider, foremanDetailsTarget);
        state.View = TuiView.Chat;
        return true;
    }

    if (ViewCommand.TryParse(command, out var viewTarget))
    {
        AnsiConsole.Clear();
        ViewCommand.Run(viewTarget, settings.VaultRoot, repoRoot);
        state.View = TuiView.Chat;
        return true;
    }

    if (command.Equals("/hire", StringComparison.OrdinalIgnoreCase))
    {
        // Scoped to /hire, not app startup: before Phase 3's FirstRunWizard exists,
        // VaultRoot is only ever set by hand, and a startup-level gate would make
        // the app unusable standalone. Phase 3 makes this a pure backstop.
        if (string.IsNullOrWhiteSpace(settings.VaultRoot) || !Directory.Exists(settings.VaultRoot))
        {
            AnsiConsole.MarkupLine("[yellow]No Vault is configured -- run [bold]/settings[/] (or set --vault-root) before hiring a Foreman.[/]");
            AnsiConsole.Markup("[grey]Press enter to continue...[/]");
            Console.ReadLine();
            state.View = TuiView.Chat;
            return true;
        }

        AnsiConsole.Clear();
        await HireWizard.Run(
            foremanDirectory, jobsiteDirectory, jobRegistry, availableProviderIds, repoRoot, settings.VaultRoot,
            mcpOptionsByProvider, runner, cts.Token);
        AnsiConsole.Markup("[grey]Press enter to continue...[/]");
        Console.ReadLine();
        state.View = TuiView.Chat;
        return true;
    }

    if (command.Equals("/fire", StringComparison.OrdinalIgnoreCase))
    {
        AnsiConsole.Clear();
        await FireWizard.Run(foremanDirectory, jobsiteDirectory, jobRegistry, repoRoot, settings.VaultRoot, worktreeManager, cts.Token);

        // A fired Foreman must not stay the drive target.
        if (state.DrivenForeman is not null && foremanDirectory.Find(state.DrivenForeman) is null)
        {
            DriveCommands.StopDriving(state);
        }

        AnsiConsole.Markup("[grey]Press enter to continue...[/]");
        Console.ReadLine();
        state.View = TuiView.Chat;
        return true;
    }

    if (command.StartsWith('/'))
    {
        var stubLabel = command[1..];
        state.View = TuiView.Stub;
        state.StubLabel = stubLabel;
        return true;
    }

    // The one dispatch path, for GC and for a driven Foreman alike. Both go
    // through JobRegistry.StartJob, which sends on the single shared
    // LiveAgentRegistry -- so GC never ends up with two divergent conversations,
    // and a driven Foreman's turn queues behind its own in-flight work on that
    // Foreman's semaphore exactly like any dispatched task.
    var target = state.DrivenForeman ?? settings.GcForemanName;

    state.View = TuiView.Chat;
    state.TranscriptFor(target).Add(new TranscriptLine("Boss", input));

    try
    {
        // Returns immediately with a job id; the turn runs in the background and
        // reports back through IJobStatusSink, which this loop is draining.
        pendingBossTurns.Track(jobRegistry.StartJob(target, input), target);
    }
    catch (Exception ex)
    {
        state.TranscriptFor(target).Add(new TranscriptLine("home office", ex.Message, IsError: true));
    }

    return true;
}

// The most recent worktree belonging to this Foreman or one of its Workers.
// Foremen themselves work in their configured directory; a worktree turns up
// once they spawn a Worker, which is what the passive column is watching.
static string? ResolveWorktreePath(JobRegistry jobs, string foremanName) =>
    jobs.GetAllJobs()
        .Where(j => j.WorktreePath is not null && DriveCommands.BelongsTo(foremanName, j.ForemanName))
        .OrderByDescending(j => j.CreatedAt)
        .Select(j => j.WorktreePath)
        .FirstOrDefault();

async Task RefreshPassiveAsync(string? worktreePath)
{
    try
    {
        var snapshot = worktreePath is null
            ? null
            : await gitInspector.InspectAsync(worktreePath, cts.Token);

        await events.Writer.WriteAsync(new BossEvent.PassiveRefreshed(snapshot), cts.Token);
    }
    catch (OperationCanceledException)
    {
    }
    catch (ChannelClosedException)
    {
    }
    finally
    {
        Interlocked.Exchange(ref passiveRefreshInFlight, 0);
    }
}

static async Task PumpInputAsync(ChannelReader<string> source, ChannelWriter<BossEvent> sink, CancellationToken ct)
{
    try
    {
        await foreach (var line in source.ReadAllAsync(ct))
        {
            await sink.WriteAsync(new BossEvent.InputLine(line), ct);
        }

        await sink.WriteAsync(new BossEvent.InputClosed(), ct);
    }
    catch (OperationCanceledException)
    {
    }
    catch (ChannelClosedException)
    {
    }
}

// IJobStatusSink has been published to since it was built and read by nothing.
// This is the read side: every transition both re-renders the dashboard and gets
// inspected against the pending Boss-turn set.
static async Task PumpJobStatusAsync(ChannelReader<JobRecord> source, ChannelWriter<BossEvent> sink, CancellationToken ct)
{
    try
    {
        await foreach (var record in source.ReadAllAsync(ct))
        {
            await sink.WriteAsync(new BossEvent.JobTransition(record), ct);
        }
    }
    catch (OperationCanceledException)
    {
    }
    catch (ChannelClosedException)
    {
    }
}

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
