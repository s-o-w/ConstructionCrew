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
using ConstructionCrew.Providers.Activity;
using ConstructionCrew.HomeOffice;
using Spectre.Console;

var repoRoot = RepoPaths.FindRepoRoot(AppContext.BaseDirectory);
var settings = AppSettingsLoader.Load(repoRoot, args);

AnsiConsole.Write(new Rule("[bold yellow]ConstructionCrew[/]").LeftJustified());

// Registered CLIs resolvable on PATH. No "id != gemini" filter needed:
// GeminiProvider reports IsImplemented == false itself. Cached to
// state/tools.json; /settings re-probes. Resolved before the roster loads
// because first run needs this list to hire a GC.
var providerRegistry = ProviderRegistry.Default(settings.StateDirectory);
var availableProviderIds = providerRegistry.AvailableIds();

// A missing roster file means a fresh install, so run first-run setup instead
// of failing. Distinct from the "gcConfig is null" branch below: that means an
// existing file with no GC entry, which stays a hard fail.
if (!File.Exists(settings.ForemenConfigPath))
{
    string? resolvedVaultRoot;
    try
    {
        resolvedVaultRoot = FirstRunWizard.Run(repoRoot, settings, availableProviderIds);
    }
    catch (Exception ex)
    {
        // First run is the one wizard that runs before the TUI is up, so a
        // piped run or CI with no terminal can hit it. Spectre throws rather
        // than degrading there; report it plainly instead of a stack trace.
        AnsiConsole.MarkupLine($"[red]First-run setup could not run:[/] {Markup.Escape(ex.Message)}");
        AnsiConsole.MarkupLine("[grey]It needs an interactive terminal. Copy config/foremen.yaml.example to config/foremen.yaml to set the roster up by hand instead.[/]");
        return 1;
    }

    if (resolvedVaultRoot is null)
    {
        AnsiConsole.MarkupLine("[yellow]First-run setup didn't finish -- nothing to run without a GC.[/]");
        return 1;
    }

    // AppSettings is a record; nothing reloads it from disk. Without this
    // reassignment, ${vaultRoot} would fail to expand on the next line and
    // /hire's Vault guard would still see null.
    settings = settings with { VaultRoot = resolvedVaultRoot };
}

// Both instructions templates require every crew member to read the Boss's
// crew preferences file. Scaffolding alone doesn't guarantee it exists (it
// only runs for "scaffold a new Vault"), so ensure this one file on every
// start for whatever vault is configured.
//
// Never fatal: a missing preferences file is a valid outcome for the crew;
// refusing to start is not.
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

// GC.md is no longer shipped; it's rendered on first need. First run's call to
// EnsureGcInstructions only fires when foremen.yaml is missing, so an existing
// roster never re-triggers it. Ensure it here too, before the loader (which
// hard-fails on a missing instructionsFilePath) can see it missing.
if (!string.IsNullOrWhiteSpace(settings.VaultRoot) && Directory.Exists(settings.VaultRoot))
{
    try
    {
        FirstRunWizard.EnsureGcInstructions(repoRoot, settings.VaultRoot, settings.GcForemanName, availableProviderIds);
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

// A roster hired before instructions moved into the Vault still names the old
// repoRoot/config/instructions/ path. Migrate on every start (a no-op once
// migrated) and use the RETURNED list: foremenSeed is stale for anyone just
// migrated, since the file it named has moved. Never fatal: a read-only vault
// keeps running on old paths instead of refusing to start.
if (!string.IsNullOrWhiteSpace(settings.VaultRoot) && Directory.Exists(settings.VaultRoot))
{
    try
    {
        var migration = InstructionsMigration.MigrateToVault(repoRoot, settings.VaultRoot, settings.ForemenConfigPath, foremenSeed);
        foremenSeed = migration.Foremen;

        if (migration.MigratedForemen.Count > 0)
        {
            AnsiConsole.MarkupLine(
                $"[grey]Migrated instructions files into the Vault for: {Markup.Escape(string.Join(", ", migration.MigratedForemen))}.[/]");
        }
    }
    catch (Exception ex)
    {
        AnsiConsole.MarkupLine($"[yellow]Could not migrate instructions files into the Vault:[/] {Markup.Escape(ex.Message)}");
    }
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
// Every registered provider, not just available ones: a foremen.yaml naming an
// uninstalled CLI then fails with that CLI's own error, not a misleading
// "no provider registered" message.
var agentFactory = new LocalCliAgentFactory(providerRegistry.Registered, runner);
// Built before JobRegistry: JobRegistry needs this instance for spawn_worker's
// worktree-per-Worker mechanism, and HomeOfficeHost registers the same
// instance for the three worktree tools.
var worktreeManager = new WorktreeManager(runner);
// One LiveAgentRegistry per process, so GC never ends up with two divergent
// conversations.
var liveAgents = new LiveAgentRegistry(agentFactory);
var statusSink = new JobStatusSink();
var runtimeOptions = new JobRegistryRuntimeOptions(settings.StateDirectory);
// These three live outside HomeOffice's reference graph, so Program.cs
// constructs them and hands JobRegistry the instances directly.
var notificationOptions = new HomeOfficeNotificationOptions(settings.NotificationsCommand);
var runLogWriter = new RunLogWriter();
// After runtimeOptions: its constructor creates StateDirectory, so the first
// append here cannot throw.
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
// implementation and hand HomeOffice an instance: HomeOffice has no
// ProjectReference to ConstructionCrew.Graph.
var vaultGraph = new VaultGraph();
var vaultOptions = new HomeOfficeVaultOptions(settings.VaultRoot);
// Same rule: WorkorderReader lives in Config, which HomeOffice doesn't reference.
var workorderReader = new WorkorderReader();
// Same rule again: SitrepWriter lives in Config.
var sitrepWriter = new SitrepWriter();

Directory.CreateDirectory(settings.StateDirectory);

using var cts = new CancellationTokenSource();
var homeOffice = await HomeOfficeHost.StartAsync(jobRegistry, foremanDirectory, jobsiteDirectory, vaultOptions, workorderReader, worktreeManager, sitrepWriter, vaultGraph, settings.HomeOfficePort, cts.Token);

// Not surfaced in the TUI itself (it used to be, and was just footer clutter).
// One plain-console line before the TUI takes over, for telling a second
// instance (e.g. a worktree build, an overridden port) apart from the live one.
var isDebug = args.Contains("--debug", StringComparer.OrdinalIgnoreCase);
if (isDebug)
{
    AnsiConsole.MarkupLine($"[grey]Home Office listening on {homeOffice.BaseAddress}[/]");
    AnsiConsole.Markup("[grey]Press enter to continue...[/]");
    Console.ReadLine();
}

// Every Foreman needs Home Office's MCP config to call list_foremen/dispatch_task/
// spawn_worker/ask_foreman, not just GC. Each available provider gets its own
// config in its own shape, then every hired Foreman is stamped with the wiring
// for the provider it runs.
var mcpOptionsByProvider = WriteMcpWiring(providerRegistry, settings.GeneratedConfigDirectory, homeOffice.BaseAddress);

// A local function, not a straight-line loop: /settings re-probes and
// reassigns mcpOptionsByProvider, and the closure captures the variable (not a
// snapshot), so this always reads the current dictionary.
void StampMcpWiring()
{
    foreach (var foreman in foremanDirectory.All().ToList())
    {
        var updated = foreman;
        var repairs = new List<string>();

        if (mcpOptionsByProvider.TryGetValue(foreman.Provider, out var mcpOptions))
        {
            var merged = new Dictionary<string, string>(foreman.ProviderOptions);
            foreach (var option in mcpOptions)
            {
                merged[option.Key] = option.Value;
            }

            updated = updated with { ProviderOptions = merged };
        }

        // Every role, not just GC: a Claude crew member hired before session
        // accounting existed has no outputFormat, so its turns report no
        // session_id, so there is nothing to resume against or to watch. Same
        // "an existing roster never picks up a ProviderDefaults change" gap the
        // GC block below repairs, and repaired the same way.
        var accounted = ProviderDefaults.EnsureSessionAccounting(foreman.Provider, updated.ProviderOptions);
        if (!ReferenceEquals(accounted, updated.ProviderOptions))
        {
            updated = updated with { ProviderOptions = new Dictionary<string, string>(accounted) };
            repairs.Add("session accounting");
        }

        // GC only. An existing roster never picks up a ProviderDefaults change,
        // because GcToolPolicy is consulted only at first-run hire. That's why
        // GC can stop to ask for interactive approval on a Home Office tool:
        // under `claude -p` a tool outside --allowedTools is auto-denied.
        // Repair and persist it here.
        if (foreman.Role == CrewRole.GC)
        {
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
        }

        if (repairs.Count > 0)
        {
            ForemanConfigWriter.RemoveForeman(settings.ForemenConfigPath, foreman.Name);
            ForemanConfigWriter.AppendForeman(settings.ForemenConfigPath, updated, repoRoot, settings.VaultRoot);

            AnsiConsole.MarkupLine(
                $"[grey]Repaired {Markup.Escape(foreman.Name)}'s config in " +
                $"{Markup.Escape(settings.ForemenConfigPath)}: {Markup.Escape(string.Join(", ", repairs))}.[/]");
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

// Boss turns dispatched but not yet reported back. Exists so JobRegistry
// doesn't need a public completion callback for the TUI's benefit.
var pendingBossTurns = new PendingBossTurns();

// Read-only git for the passive column, on the same ICliProcessRunner
// WorktreeManager uses. No second process seam.
var gitInspector = new GitWorkspaceInspector(runner);

// One channel, three producers (input pump, IJobStatusSink pump, passive
// refresh), one consumer: this loop. Single-reader lets every DashboardState
// mutation happen on one thread with no locking.
var events = Channel.CreateUnbounded<BossEvent>(new UnboundedChannelOptions { SingleReader = true });

_ = PumpJobStatusAsync(statusSink.Reader, events.Writer, cts.Token);
_ = PumpActivityHeartbeatAsync(events.Writer, cts.Token);

// 0 = no passive refresh in flight. Interlocked: the refresh completes on a
// thread-pool thread while the loop may already be starting another.
var passiveRefreshInFlight = 0;

// A second guard, not a shared one: a slow transcript read must never hold up
// the git read, or the other way round.
var activityRefreshInFlight = 0;

// Separate guard for GC's own activity read: it runs regardless of WatchSubject
// so it must never block or be blocked by the watch-subject read.
var gcActivityRefreshInFlight = 0;

// Which engines keep a session transcript this app can tail. Built once: the
// lookup is pure, and /watch consults it before setting a watch that could
// only ever render blank.
var activityReaders = ForemanActivityReaders.Default();

var running = true;
var inputBuffer = new System.Text.StringBuilder();

// Pre-allocate the layout tree once; leaf content is swapped in place on every
// refresh so LiveDisplay can use cursor-up + overwrite instead of a full clear.
var (liveRoot, liveBody) = Dashboard.CreateLayout();
Console.Write("\x1b[2J\x1b[H");
Dashboard.UpdateLayout(liveRoot, liveBody, foremanDirectory, jobsiteDirectory, jobRegistry, state, "");

await AnsiConsole.Live(liveRoot)
    .AutoClear(false)
    .StartAsync(async ctx =>
    {
        ctx.Refresh();

        while (running)
        {
            var needsRefresh = false;

            // ── keyboard ──────────────────────────────────────────────────
            // Console.ReadKey(intercept:true) reads one character at a time
            // without echoing, so the input buffer is rendered as part of the
            // footer layout instead of raw terminal output.
            while (Console.KeyAvailable)
            {
                var key = Console.ReadKey(intercept: true);
                switch (key.Key)
                {
                    case ConsoleKey.Enter:
                        var line = inputBuffer.ToString().Trim();
                        inputBuffer.Clear();
                        needsRefresh = true;

                        if (!string.IsNullOrWhiteSpace(line))
                        {
                            var watchedBefore = state.WatchSubject;
                            var drivenBefore = state.DrivenForeman;
                            try
                            {
                                running = await HandleBossLine(line);
                            }
                            catch (Exception ex)
                            {
                                state.ActiveTranscript.Add(new TranscriptLine("home office", ex.Message, IsError: true));
                            }

                            // Cursor-home so ctx.Refresh() redraws the layout
                            // cleanly after any modal that wrote to the screen.
                            // LiveDisplay's "move up N lines" from row 0 is a
                            // no-op, so it clears from the top and redraws.
                            Console.Write("\x1b[H");

                            if (running &&
                                (!string.Equals(drivenBefore, state.DrivenForeman, StringComparison.OrdinalIgnoreCase) ||
                                 !string.Equals(watchedBefore, state.WatchSubject, StringComparison.OrdinalIgnoreCase)))
                            {
                                if (state.WatchSubject is { } newSubject)
                                {
                                    if (Interlocked.CompareExchange(ref passiveRefreshInFlight, 1, 0) == 0)
                                        _ = RefreshPassiveAsync(ResolveWorktreePath(jobRegistry, foremanDirectory, newSubject));
                                    if (Interlocked.CompareExchange(ref activityRefreshInFlight, 1, 0) == 0)
                                        _ = RefreshActivityAsync(newSubject);
                                }
                            }
                        }
                        break;

                    case ConsoleKey.Backspace:
                        if (inputBuffer.Length > 0)
                            inputBuffer.Remove(inputBuffer.Length - 1, 1);
                        needsRefresh = true;
                        break;

                    case ConsoleKey.C when (key.Modifiers & ConsoleModifiers.Control) != 0:
                        running = false;
                        break;

                    default:
                        if (key.KeyChar != '\0' && !char.IsControl(key.KeyChar))
                        {
                            inputBuffer.Append(key.KeyChar);
                            needsRefresh = true;
                        }
                        break;
                }
            }

            // ── background events ──────────────────────────────────────────
            var refreshPassive = false;

            while (events.Reader.TryRead(out var bossEvent))
            {
                switch (bossEvent)
                {
                    case BossEvent.JobTransition transition:
                        // A milestone sitrep's synthetic record (JobRegistry.NotifyMilestone)
                        // queues into the Inbox instead of the chat transcript.
                        if (transition.Record.JobId.StartsWith("milestone:", StringComparison.Ordinal))
                        {
                            state.Inbox.Add(new InboxItem(
                                transition.Record.ForemanName, transition.Record.Summary ?? string.Empty, transition.Record.CreatedAt));
                        }
                        else if (pendingBossTurns.TryTakeCompletion(transition.Record, out var speaker, out var completion))
                        {
                            state.TranscriptFor(speaker).Add(completion);
                        }
                        refreshPassive = true;
                        needsRefresh = true;
                        break;

                    case BossEvent.InputClosed:
                        running = false;
                        break;

                    case BossEvent.PassiveRefreshed refreshed:
                        state.Passive = refreshed.Snapshot;
                        needsRefresh = true;
                        break;

                    case BossEvent.ActivityRefreshed refreshedActivity:
                        if (refreshedActivity.ForemanName.Equals(state.GcForemanName, StringComparison.OrdinalIgnoreCase))
                            state.GcActivity = refreshedActivity.Snapshot;
                        else
                            state.Activity = refreshedActivity.Snapshot;
                        needsRefresh = true;
                        break;

                    case BossEvent.ActivityHeartbeat:
                        // Kick off async reads; the resulting ActivityRefreshed
                        // events will set needsRefresh when they arrive. The
                        // heartbeat itself does NOT trigger a redraw -- that's
                        // what was wiping the input line.
                        if (jobRegistry.IsForemanBusy(state.GcForemanName))
                        {
                            if (Interlocked.CompareExchange(ref gcActivityRefreshInFlight, 1, 0) == 0)
                                _ = RefreshGcActivityAsync(state.GcForemanName);
                        }
                        else if (state.GcActivity is not null)
                        {
                            state.GcActivity = null;
                            needsRefresh = true;
                        }

                        if (state.WatchSubject is { } heartbeatSubject)
                        {
                            if (Interlocked.CompareExchange(ref passiveRefreshInFlight, 1, 0) == 0)
                                _ = RefreshPassiveAsync(ResolveWorktreePath(jobRegistry, foremanDirectory, heartbeatSubject));
                            if (Interlocked.CompareExchange(ref activityRefreshInFlight, 1, 0) == 0)
                                _ = RefreshActivityAsync(heartbeatSubject);
                        }
                        break;
                }

                if (!running) break;
            }

            // Not triggered by PassiveRefreshed/ActivityRefreshed themselves:
            // that would loop forever. Trigger on job transitions and commands
            // that change the watch subject.
            if (refreshPassive && running && state.WatchSubject is { } watchSubject)
            {
                if (Interlocked.CompareExchange(ref passiveRefreshInFlight, 1, 0) == 0)
                    _ = RefreshPassiveAsync(ResolveWorktreePath(jobRegistry, foremanDirectory, watchSubject));
                if (Interlocked.CompareExchange(ref activityRefreshInFlight, 1, 0) == 0)
                    _ = RefreshActivityAsync(watchSubject);
            }

            // ── render ────────────────────────────────────────────────────
            if (needsRefresh && running)
            {
                Dashboard.UpdateLayout(liveRoot, liveBody, foremanDirectory, jobsiteDirectory, jobRegistry, state, inputBuffer.ToString());
                ctx.Refresh();
            }

            if (running)
            {
                try { await Task.Delay(16, cts.Token); }
                catch (OperationCanceledException) { break; }
            }
        }
    });

cts.Cancel();
await homeOffice.DisposeAsync();
return 0;

// Returns false when the Boss asked to leave. Never awaits an agent turn: a
// dispatch is JobRegistry.StartJob, which returns a job id and runs in the
// background so the Boss can keep typing.
async Task<bool> HandleBossLine(string input)
{
    if (string.IsNullOrWhiteSpace(input))
    {
        return true;
    }

    var command = input.Trim();

    // Drive routing first: /exit means "leave this Foreman" while driving,
    // "quit" otherwise, and /drive must not fall through to the stub below.
    switch (DriveCommands.Apply(state, command, foremanDirectory.Find, jobRegistry.GetAllJobs()))
    {
        case BossCommandResult.Quit:
            return false;
        case BossCommandResult.Handled:
            return true;
    }

    // After drive routing, before the rest: /watch is the read-only sibling of
    // /drive and must not fall through to the stub handler either.
    if (WatchCommand.Apply(state, command, foremanDirectory.Find, activityReaders) == BossCommandResult.Handled)
    {
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

    // Tab set first, cleared last, so the strip shows "memory" while the modal
    // browser owns the screen. Browser stays within the crew's vault folders,
    // see MemoryBrowser.Roots.
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
        AnsiConsole.MarkupLine("[grey]/chat  /tasks  /monitor  /memory  /hire  /fire  /foreman <Name>  /view <path>  /preferences [add]  /inbox  /watch <Name>  /drive <Foreman>  /settings  /migrate  /exit (bare \"quit\" or \"exit\" also work) -- anything else is sent to the GC (or the driven Foreman) as a message.[/]");
        AnsiConsole.MarkupLine("[grey]/watch shows what someone is doing without changing where your typing goes; /drive redirects it and shows the same feed.[/]");
        AnsiConsole.Markup("[grey]Press enter to continue...[/]");
        Console.ReadLine();
        return true;
    }

    if (command.Equals("/settings", StringComparison.OrdinalIgnoreCase))
    {
        AnsiConsole.Clear();
        AnsiConsole.Write(new Rule("[bold yellow]settings[/]").LeftJustified());

        // No inline setup offer when a Vault is already configured: this
        // command only unblocks an unconfigured Boss.
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

        // Re-probes PATH (a CLI installed since startup shows up) and rewrites state/tools.json.
        var probes = providerRegistry.Refresh();
        availableProviderIds = providerRegistry.AvailableIds();
        mcpOptionsByProvider = WriteMcpWiring(providerRegistry, settings.GeneratedConfigDirectory, homeOffice.BaseAddress);

        // The re-probe just rewrote MCP configs and may have wired a new
        // provider, so the roster's stamped paths are stale until this runs.
        // Also re-heals GC's tool policy, same as startup.
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

    if (command.Equals("/migrate", StringComparison.OrdinalIgnoreCase))
    {
        AnsiConsole.Clear();
        AnsiConsole.Write(new Rule("[bold yellow]migrate instructions into the vault[/]").LeftJustified());

        if (string.IsNullOrWhiteSpace(settings.VaultRoot) || !Directory.Exists(settings.VaultRoot))
        {
            AnsiConsole.MarkupLine("[yellow]No Vault configured -- nothing to migrate into.[/]");
        }
        else
        {
            // Same routine as the startup self-heal, re-triggered on demand
            // without restarting the app.
            var migration = InstructionsMigration.MigrateToVault(
                repoRoot, settings.VaultRoot, settings.ForemenConfigPath, foremanDirectory.All().ToList());

            foreach (var updated in migration.Foremen)
            {
                foremanDirectory.Add(updated);
            }

            if (migration.TemplatesEnsured.Count > 0)
            {
                AnsiConsole.MarkupLine($"[green]Seeded templates:[/] {Markup.Escape(string.Join(", ", migration.TemplatesEnsured))}");
            }

            AnsiConsole.MarkupLine(migration.MigratedForemen.Count > 0
                ? $"[green]Migrated:[/] {Markup.Escape(string.Join(", ", migration.MigratedForemen))}"
                : "[grey]Already up to date -- nothing to migrate.[/]");
        }

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

    if (command.Equals("/preferences", StringComparison.OrdinalIgnoreCase) ||
        command.StartsWith("/preferences ", StringComparison.OrdinalIgnoreCase))
    {
        var preferencesArgument = command.Length > "/preferences".Length ? command["/preferences".Length..].Trim() : null;
        AnsiConsole.Clear();
        PreferencesCommand.Run(preferencesArgument, settings.VaultRoot, repoRoot);
        state.View = TuiView.Chat;
        return true;
    }

    if (command.Equals("/inbox", StringComparison.OrdinalIgnoreCase))
    {
        AnsiConsole.Clear();
        InboxCommand.Run(state);
        state.View = TuiView.Chat;
        return true;
    }

    if (command.Equals("/job", StringComparison.OrdinalIgnoreCase))
    {
        AnsiConsole.Clear();
        JobDetailCommand.Run(jobRegistry);
        state.View = TuiView.Tasks;
        return true;
    }

    if (command.Equals("/hire", StringComparison.OrdinalIgnoreCase))
    {
        // Scoped to /hire, not startup: a startup-level gate would make the
        // app unusable before a Vault is configured. Pure backstop now that
        // FirstRunWizard exists.
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

    // One dispatch path for GC and a driven Foreman alike. Both go through
    // JobRegistry.StartJob on the single shared LiveAgentRegistry, so GC never
    // ends up with two divergent conversations.
    var target = state.DrivenForeman ?? settings.GcForemanName;

    state.View = TuiView.Chat;
    state.TranscriptFor(target).Add(new TranscriptLine("Boss", input));

    try
    {
        // Returns immediately with a job id; the turn runs in the background
        // and reports back through IJobStatusSink, which this loop drains.
        pendingBossTurns.Track(jobRegistry.StartJob(target, input), target);
    }
    catch (Exception ex)
    {
        state.TranscriptFor(target).Add(new TranscriptLine("home office", ex.Message, IsError: true));
    }

    return true;
}

// The most recent worktree belonging to this Foreman or one of its Workers,
// or their configured working directory when no worktree has been allocated.
// Claude Code Foremen create worktrees via the worktree tool; Codex Foremen
// work directly in their configured directory and never set WorktreePath.
static string? ResolveWorktreePath(JobRegistry jobs, ForemanDirectory foremen, string foremanName) =>
    jobs.GetAllJobs()
        .Where(j => j.WorktreePath is not null && DriveCommands.BelongsTo(foremanName, j.ForemanName))
        .OrderByDescending(j => j.CreatedAt)
        .Select(j => j.WorktreePath)
        .FirstOrDefault()
    ?? foremen.Find(foremanName)?.WorkingDirectory;

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

// The activity half of the side panel: resolve the name's engine and session
// id, pick the reader that understands that engine, and post what it read.
// Same shape as RefreshPassiveAsync deliberately -- read external state off the
// loop, report back as a BossEvent, let the next redraw pick it up.
async Task RefreshActivityAsync(string foremanName)
{
    try
    {
        var snapshot = ReadActivity(foremanName);
        await events.Writer.WriteAsync(new BossEvent.ActivityRefreshed(foremanName, snapshot), cts.Token);
    }
    catch (OperationCanceledException)
    {
    }
    catch (ChannelClosedException)
    {
    }
    finally
    {
        Interlocked.Exchange(ref activityRefreshInFlight, 0);
    }
}

// GC-specific variant: clears gcActivityRefreshInFlight (not activityRefreshInFlight)
// so GC's guard and the watch-subject's guard stay independent.
async Task RefreshGcActivityAsync(string foremanName)
{
    try
    {
        var snapshot = ReadActivity(foremanName);
        await events.Writer.WriteAsync(new BossEvent.ActivityRefreshed(foremanName, snapshot), cts.Token);
    }
    catch (OperationCanceledException)
    {
    }
    catch (ChannelClosedException)
    {
    }
    finally
    {
        Interlocked.Exchange(ref gcActivityRefreshInFlight, 0);
    }
}

ForemanActivitySnapshot? ReadActivity(string foremanName)
{
    // Nothing dispatched to this name yet, so there is no conversation to read.
    // Said plainly rather than left blank: an empty panel reads as "idle".
    var info = jobRegistry.GetActivityInfo(foremanName);
    if (info is null)
    {
        return new ForemanActivitySnapshot("no turns yet", null, "nothing dispatched to this one yet");
    }

    var reader = activityReaders.For(info.Value.Engine);
    if (reader is null)
    {
        return new ForemanActivitySnapshot(
            "no activity feed", null, $"{info.Value.Engine} keeps no readable transcript");
    }

    if (string.IsNullOrWhiteSpace(info.Value.SessionId))
    {
        // Session ID is only extracted from stderr after the process exits.
        // While the job is in-flight, try to find the active transcript by CWD scan.
        var cwd = foremanDirectory.Find(foremanName)?.WorkingDirectory;
        if (!string.IsNullOrWhiteSpace(cwd))
        {
            var liveActivity = reader.TryReadForCwd(cwd);
            if (liveActivity is not null) return liveActivity;
        }

        return new ForemanActivitySnapshot("starting up", null, "its first turn has not reported a session yet");
    }

    var workingDirectory = foremanDirectory.Find(foremanName)?.WorkingDirectory ?? string.Empty;
    return reader.Read(info.Value.SessionId!, workingDirectory);
}

// Fires ActivityHeartbeat every 500 ms while the app is running. The loop
// uses these ticks to read GC's live activity for the main pane and the watch
// subject's activity for the side panel, without waiting for a job transition.
static async Task PumpActivityHeartbeatAsync(ChannelWriter<BossEvent> sink, CancellationToken ct)
{
    try
    {
        while (true)
        {
            await Task.Delay(500, ct);
            await sink.WriteAsync(new BossEvent.ActivityHeartbeat(), ct);
        }
    }
    catch (OperationCanceledException)
    {
    }
    catch (ChannelClosedException)
    {
    }
}

// Read side of IJobStatusSink: every transition re-renders the dashboard and
// is checked against the pending Boss-turn set.
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

// Writes each available provider's Home Office config in its own shape and
// returns the ProviderOptions to stamp onto Foremen running it. A provider
// with no verified MCP shape is warned about, not fatal: it still works, it
// just can't call Home Office tools.
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
