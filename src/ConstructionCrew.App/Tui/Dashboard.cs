using ConstructionCrew.Config;
using ConstructionCrew.Core.Models;
using ConstructionCrew.HomeOffice;
using ConstructionCrew.Providers.Activity;
using Spectre.Console;
using Spectre.Console.Rendering;

namespace ConstructionCrew.App.Tui;

/// <summary>
/// Renders the full-screen shell. Supports both a one-shot <see cref="Render"/>
/// path (used for non-interactive runs) and a LiveDisplay path via
/// <see cref="CreateLayout"/> + <see cref="UpdateLayout"/>: allocate once,
/// mutate leaf nodes in place, call <c>ctx.Refresh()</c> — no full-screen clear.
/// </summary>
public static class Dashboard
{
    // "memory" is modal (the browser takes over the screen, like /view and
    // the wizards) and its tab just shows the Boss is in it; "monitor"
    // renders in place off the registries.
    private static readonly (string Id, string Label)[] Tabs =
    [
        ("chat", "chat"),
        ("tasks", "tasks"),
        ("hire", "hire"),
        ("memory", "memory"),
        ("monitor", "monitor"),
    ];

    private static readonly HashSet<string> LiveTabs =
        new(StringComparer.OrdinalIgnoreCase) { "chat", "tasks", "hire", "memory", "monitor" };

    /// <summary>
    /// Allocates the Layout tree structure once. Call once at startup, then pass
    /// root and body to <see cref="UpdateLayout"/> on every redraw. The tree
    /// structure (splits, sizes) never changes; only leaf-node content changes.
    /// </summary>
    public static (Layout Root, Layout Body) CreateLayout()
    {
        var root = new Layout("root").SplitRows(
            new Layout("header").Size(3),
            new Layout("body"),
            new Layout("footer").Size(2));

        var body = new Layout("body").SplitColumns(
            new Layout("sidebar").Size(34),
            new Layout("main"));

        body["sidebar"].SplitRows(
            new Layout("sidebar-roster"),
            new Layout("sidebar-commands").Size(13));

        root["body"].Update(body);

        return (root, body);
    }

    /// <summary>
    /// Updates all leaf-node content in the pre-allocated Layout tree.
    /// Does NOT clear the screen — call <c>ctx.Refresh()</c> after this
    /// to push the changes to the terminal without flicker.
    /// </summary>
    public static void UpdateLayout(
        Layout root, Layout body,
        ForemanDirectory foremen, JobsiteDirectory jobsites, JobRegistry jobs,
        DashboardState state, string inputBuffer)
    {
        root["header"].Update(BuildHeader());
        body["sidebar"]["sidebar-roster"].Update(BuildRoster(foremen, jobsites, jobs));
        body["sidebar"]["sidebar-commands"].Update(BuildCommandsPanel());
        body["main"].Update(BuildMain(foremen, jobsites, jobs, state));
        root["footer"].Update(new Rows(
            new Markup(FooterFor(state.DrivenForeman, state.Inbox.Count(i => !i.Read), state.WatchedForeman)),
            new Markup(BuildInputLine(state.DrivenForeman, inputBuffer))));
    }

    private static string BuildInputLine(string? drivenForeman, string inputBuffer)
    {
        var prompt = drivenForeman is null
            ? "[cyan]Boss[/]"
            : $"[cyan]Boss[/][grey][{Markup.Escape(drivenForeman)}][/]";
        return $"{prompt}[grey]>[/] {Markup.Escape(inputBuffer)}[grey]▋[/]";
    }

    public static void Render(ForemanDirectory foremen, JobsiteDirectory jobsites, JobRegistry jobs, DashboardState state)
    {
        ClearScreen();
        var (root, body) = CreateLayout();
        UpdateLayout(root, body, foremen, jobsites, jobs, state, string.Empty);
        AnsiConsole.Write(root);
        PositionCursorOnPromptRow();
    }

    /// <summary>
    /// The one-line command hint under the board. Named rather than inlined
    /// so a test can assert the command list without scraping a rendered layout.
    /// </summary>
    internal static string FooterFor(string? drivenForeman, int unreadInboxCount = 0, string? watchedForeman = null)
    {
        if (drivenForeman is not null)
        {
            var alsoWatching = watchedForeman is null
                ? string.Empty
                : $"[grey], watching [/][yellow]{Markup.Escape(watchedForeman)}[/]";
            return $"[grey]driving [/][yellow]{Markup.Escape(drivenForeman)}[/]{alsoWatching}[grey] -- /exit returns to GC[/]";
        }

        // Watching without driving is the state most worth spelling out: input
        // is still going to GC, which is not obvious from a panel full of
        // somebody else's activity.
        if (watchedForeman is not null)
        {
            return $"[grey]watching [/][yellow]{Markup.Escape(watchedForeman)}[/]" +
                   "[grey] -- you are still talking to GC; /watch stops, /drive redirects[/]";
        }

        var badge = unreadInboxCount > 0 ? $"  [yellow]{unreadInboxCount} new in /inbox[/]" : string.Empty;
        return $"[grey]commands in sidebar -- /help for full list[/]{badge}";
    }

    private static void PositionCursorOnPromptRow()
    {
        if (Console.IsOutputRedirected)
        {
            return;
        }

        try
        {
            // Same value Spectre's Layout.Render used to size "root"; anything
            // else risks a 1-row mismatch with the layout's blank last row.
            var lastRow = AnsiConsole.Profile.Height;
            Console.Out.Write($"\x1b[{lastRow};1H");
        }
        catch (IOException)
        {
        }
    }

    private static void ClearScreen()
    {
        if (Console.IsOutputRedirected)
        {
            return;
        }

        try
        {
            // Raw ANSI clear-screen + cursor-home, not Console.Clear()/
            // AnsiConsole.Clear(): those go through Win32 console APIs that
            // don't reliably work under a pty-based terminal like mintty/Git
            // Bash (confirmed: Console.Clear() there did not actually clear
            // the screen).
            Console.Out.Write("\x1b[2J\x1b[H");
        }
        catch (IOException)
        {
        }
    }

    private static IRenderable BuildHeader() =>
        new Panel(new Markup("[bold yellow]CONSTRUCTIONCREW[/]  [grey]-- the Boss's home office[/]"))
            .Border(BoxBorder.Rounded)
            .Expand();

    private static IRenderable BuildRoster(ForemanDirectory foremen, JobsiteDirectory jobsites, JobRegistry jobs)
    {
        var rows = new List<IRenderable>();

        // Available content rows: total height minus header(3), footer(2),
        // commands panel(13), this panel's own borders(2).
        var contentHeight = Math.Max(4, AnsiConsole.Profile.Height - 20);
        var usedRows = 0;

        var gc = foremen.Find("GC");
        if (gc is not null)
        {
            rows.Add(new Markup($"[bold]GC[/]  {StatusBadge(jobs.IsForemanBusy("GC"), jobs.IsForemanParked("GC"))}"));
            rows.Add(new Markup($"[grey]{gc.Provider}[/]"));
            rows.Add(Text.Empty);
            usedRows += 3;
        }

        var others = foremen.All()
            .Where(f => !f.Name.Equals("GC", StringComparison.OrdinalIgnoreCase))
            .ToList();
        var skipped = 0;

        for (var i = 0; i < others.Count; i++)
        {
            var foreman = others[i];
            // Estimate: plain entry = 3 rows; jobsite-colored panel entry = 5 rows.
            // Reserve 2 rows for the hint/overflow line at the bottom.
            var cost = foreman.JobsiteName is null ? 3 : 5;
            if (usedRows + cost > contentHeight - 2)
            {
                skipped = others.Count - i;
                break;
            }

            rows.Add(BuildForemanEntry(foreman, jobsites, jobs));
            rows.Add(Text.Empty);
            usedRows += cost;
        }

        rows.Add(skipped > 0
            ? new Markup($"[grey]+{skipped} more -- /hire /fire[/]")
            : new Markup("[grey]/hire to add, /fire to remove[/]"));

        return new Panel(new Rows(rows))
            .Header("[bold]site roster[/]")
            .Border(BoxBorder.Rounded)
            .Expand();
    }

    private static IRenderable BuildCommandsPanel()
    {
        var commands = new[]
        {
            "[grey]/tasks[/]    view job board",
            "[grey]/job[/]      job details",
            "[grey]/monitor[/]  crew status",
            "[grey]/memory[/]   browse notes",
            "[grey]/hire[/]     add foreman",
            "[grey]/fire[/]     remove foreman",
            "[grey]/inbox[/]    read messages",
            "[grey]/chat[/]     return to chat",
            "[grey]/watch[/]    watch foreman",
            "[grey]/drive[/]    talk to foreman",
            "[grey]/help[/]     all commands",
        };

        return new Panel(new Rows(commands.Select(c => (IRenderable)new Markup(c))))
            .Header("[bold]commands[/]")
            .Border(BoxBorder.Rounded)
            .Expand();
    }

    private static IRenderable BuildForemanEntry(ForemanConfig foreman, JobsiteDirectory jobsites, JobRegistry jobs)
    {
        var jobsiteSuffix = string.IsNullOrWhiteSpace(foreman.JobsiteName) ? "" : $" @ {foreman.JobsiteName}";
        var entry = new Rows(
            new Markup($"[bold]{Markup.Escape(foreman.Name)}[/]  {StatusBadge(jobs.IsForemanBusy(foreman.Name), jobs.IsForemanParked(foreman.Name))}"),
            new Markup($"[grey]{Markup.Escape(foreman.Provider)}{Markup.Escape(jobsiteSuffix)}[/]"));

        // The jobsite's color as a small border, so the roster shows which
        // site each Foreman belongs to at a glance.
        var jobsite = foreman.JobsiteName is null ? null : jobsites.Find(foreman.JobsiteName);
        if (jobsite is null)
        {
            return entry;
        }

        var panel = new Panel(entry).Border(BoxBorder.Rounded);
        panel.BorderStyle = new Style(foreground: JobsiteColors.ResolveForJobsite(jobsite));
        return panel;
    }

    /// <summary>
    /// Three states, not two. A parked Foreman is NOT busy (it can still take
    /// a sitrep or redirect) but isn't free either: it's blocked on the Boss,
    /// and rendering it as "idle" would hide the thing the Boss has to act on.
    /// </summary>
    internal static string StatusBadge(bool busy, bool parked = false) =>
        busy ? "[bold black on yellow] working [/]"
        : parked ? "[bold black on magenta] parked [/]"
        : "[grey on grey19] idle [/]";

    private static IRenderable BuildMain(
        ForemanDirectory foremen, JobsiteDirectory jobsites, JobRegistry jobs, DashboardState state)
    {
        var tabStrip = new Markup(string.Join("   ", Tabs.Select(t => RenderTab(t, state))));

        IRenderable content = state.View switch
        {
            TuiView.Chat => BuildChatPane(state),
            TuiView.Tasks => BuildTasks(jobs),
            TuiView.Monitor => BuildMonitor(foremen, jobsites, jobs),
            TuiView.Memory => BuildMemoryHint(),
            _ => BuildStub(state.StubLabel ?? "this"),
        };

        var mainLayout = new Layout("mainInner").SplitRows(
            new Layout("tabstrip").Size(1),
            new Layout("content"));
        mainLayout["tabstrip"].Update(new Padder(tabStrip, new Padding(1, 0)));
        mainLayout["content"].Update(content);

        return new Panel(mainLayout).Border(BoxBorder.Rounded).Expand();
    }

    private static string RenderTab((string Id, string Label) tab, DashboardState state)
    {
        var isActive = state.View switch
        {
            TuiView.Chat => tab.Id == "chat",
            TuiView.Tasks => tab.Id == "tasks",
            TuiView.Memory => tab.Id == "memory",
            TuiView.Monitor => tab.Id == "monitor",
            TuiView.Stub => string.Equals(tab.Id, state.StubLabel, StringComparison.OrdinalIgnoreCase),
            _ => false,
        };

        if (isActive)
        {
            return $"[bold black on yellow] {tab.Label} [/]";
        }

        return LiveTabs.Contains(tab.Id) ? $"[white]{tab.Label}[/]" : $"[grey]{tab.Label}[/]";
    }

    /// <summary>
    /// Watching or driving gets the chat pane a side column: what that crew
    /// member is doing right now, over a read-only <c>git status</c>/
    /// <c>git log</c> of its worktree. A Grid, not a nested Layout: Layout
    /// always renders the full console height, wrong for a cell inside the shell.
    /// </summary>
    private static IRenderable BuildChatPane(DashboardState state)
    {
        var chat = BuildChat(state);
        var hasWatchPanel = state.WatchSubject is not null;
        if (!hasWatchPanel)
        {
            return chat;
        }

        var grid = new Grid().Expand();
        grid.AddColumn(new GridColumn());
        grid.AddColumn(new GridColumn().Width(46).NoWrap());
        grid.AddRow(chat, BuildPassiveColumn(state));
        return grid;
    }

    private static IRenderable BuildPassiveColumn(DashboardState state)
    {
        var rows = new List<IRenderable>();

        // Activity first: it is the thing that changes every few seconds and
        // the reason to be watching at all. Git state sits under it as the
        // slower-moving context.
        rows.AddRange(BuildActivityRows(state.Activity));
        rows.Add(Text.Empty);

        var snapshot = state.Passive;

        if (snapshot is null)
        {
            rows.Add(new Markup("[grey]no worktree to watch[/]"));
        }
        else if (snapshot.Error is not null)
        {
            rows.Add(new Markup($"[red]{Markup.Escape(Truncate(snapshot.Error, 100))}[/]"));
        }
        else
        {
            rows.Add(new Markup($"[grey]branch[/] {Markup.Escape(snapshot.Branch ?? "?")}"));
            rows.Add(new Markup(snapshot.ChangedFiles == 0
                ? "[grey]working tree clean[/]"
                : $"[yellow]{snapshot.ChangedFiles} changed file(s)[/]"));
            rows.Add(Text.Empty);

            rows.AddRange(snapshot.RecentCommits.Count == 0
                ? [new Markup("[grey]no commits yet[/]")]
                : snapshot.RecentCommits.Select(c => (IRenderable)new Markup($"[grey]{Markup.Escape(Truncate(c, 42))}[/]")));
        }

        // The header is the watched-or-driven name either way, so driving and
        // watching the same Foreman render identically.
        return new Panel(new Rows(rows))
            .Header($"[bold]{Markup.Escape(state.WatchSubject ?? "")}[/]")
            .Border(BoxBorder.Rounded)
            .Expand();
    }

    /// <summary>
    /// The activity line, and the clock reading for it. Split out so a test can
    /// assert what each of the three states renders without scraping a layout.
    /// </summary>
    internal static IReadOnlyList<IRenderable> BuildActivityRows(ForemanActivitySnapshot? activity)
    {
        if (activity is null)
        {
            return [new Markup("[grey]reading activity...[/]")];
        }

        // An unreadable transcript is reported, not hidden: "nothing to show"
        // and "could not look" are different answers, and only one of them
        // means the Foreman is idle.
        if (activity.Error is not null)
        {
            return [new Markup($"[grey]{Markup.Escape(Truncate(activity.Error, 42))}[/]")];
        }

        var rows = new List<IRenderable>();

        if (activity.Lines is { Count: > 0 } lines)
        {
            // Chronological order (oldest first, newest last). Older lines are
            // dimmed so the most recent event reads first at a glance.
            for (var i = 0; i < lines.Count; i++)
            {
                var isNewest = i == lines.Count - 1;
                var style = isNewest ? "white" : "grey";
                rows.Add(new Markup($"[{style}]{Markup.Escape(Truncate(lines[i], 42))}[/]"));
            }
        }
        else
        {
            rows.Add(new Markup($"[white]{Markup.Escape(Truncate(activity.Summary, 42))}[/]"));
        }

        if (activity.At is { } at)
        {
            rows.Add(new Markup($"[grey]{at.ToLocalTime():HH:mm:ss}[/]"));
        }

        return rows;
    }

    private static IRenderable BuildChat(DashboardState state)
    {
        var transcript = state.ActiveTranscript;

        if (transcript.Count == 0 && state.GcActivity is null)
        {
            return new Markup(state.DrivenForeman is null
                ? "[grey]Say something to the GC to get started.[/]"
                : $"[grey]Driving {Markup.Escape(state.DrivenForeman)} -- anything you type goes to them. /exit returns to GC.[/]");
        }

        // Height-budgeted walk from the newest entry backward.
        // Reserve rows for the GC live-activity tail when it's present.
        var activityRows = BuildActivityRows(state.GcActivity);
        var activityIsLive = state.GcActivity?.Error is null && state.GcActivity?.Lines is { Count: > 0 };
        var activityHeight = activityIsLive ? activityRows.Count + 2 : 0; // +2 for separator + label

        var budget = Math.Max(4, AnsiConsole.Profile.Height - 8 - activityHeight);
        var rows = new List<IRenderable>();

        if (transcript.Count > 0)
        {
            var windowed = WindowToBudget(transcript, budget, line =>
                Pager.EstimateLines(RenderLine(line)));
            rows.AddRange(windowed.Select(RenderLine));
        }

        // GC live-activity tail: only when GC is actively working (Lines set,
        // no error) and we are in the GC conversation (not driving someone else).
        if (activityIsLive && state.DrivenForeman is null)
        {
            if (rows.Count > 0) rows.Add(Text.Empty);
            rows.Add(new Markup("[grey]GC is working...[/]"));
            rows.AddRange(activityRows);
        }

        return rows.Count == 0
            ? new Markup("[grey]Say something to the GC to get started.[/]")
            : new Rows(rows);
    }

    private static Markup RenderLine(TranscriptLine line)
    {
        var speakerStyle = line.Speaker == "Boss" ? "cyan" : line.IsError ? "red" : "green";
        return new Markup($"[bold {speakerStyle}]{line.Speaker}:[/] {Markup.Escape(line.Text)}");
    }

    /// <summary>
    /// Walks <paramref name="transcript"/> from the newest entry backward,
    /// keeping as many whole entries as fit in <paramref name="budget"/>
    /// (whatever unit <paramref name="heightOf"/> returns). The newest entry
    /// is always kept, even alone over budget -- older entries drop off
    /// first, never the latest entry's tail. Order is preserved.
    /// </summary>
    internal static IReadOnlyList<TranscriptLine> WindowToBudget(
        IReadOnlyList<TranscriptLine> transcript, int budget, Func<TranscriptLine, int> heightOf)
    {
        var kept = new List<TranscriptLine>();
        var used = 0;

        foreach (var line in transcript.Reverse())
        {
            var height = heightOf(line);
            if (kept.Count > 0 && used + height > budget)
            {
                break;
            }

            kept.Insert(0, line);
            used += height;
        }

        return kept;
    }

    private static IRenderable BuildTasks(JobRegistry jobs) =>
        new Columns(TaskColumns(jobs.GetAllJobs())
            .Select(c => BuildTaskColumn(c.Title, c.Color, c.Jobs))
            .ToArray());

    /// <summary>
    /// The task board's columns, in order. Split out from rendering so the
    /// membership rules (a Parked job is its own column, never "doing") can
    /// be asserted directly.
    /// </summary>
    internal static IReadOnlyList<(string Title, string Color, IReadOnlyList<JobRecord> Jobs)> TaskColumns(
        IReadOnlyCollection<JobRecord> all) =>
    [
        ("doing", "yellow", all.Where(j => j.Status is JobStatus.Pending or JobStatus.Running).ToList()),
        ("parked", "magenta", all.Where(j => j.Status == JobStatus.Parked).ToList()),
        ("done", "green", all.Where(j => j.Status == JobStatus.Completed).ToList()),
        ("failed", "red", all.Where(j => j.Status == JobStatus.Failed).ToList()),
    ];

    private static IRenderable BuildTaskColumn(string title, string color, IReadOnlyList<JobRecord> jobs)
    {
        var body = jobs.Count == 0
            ? new Markup("[grey]none[/]")
            : (IRenderable)new Rows(jobs.Select(j =>
            {
                var lines = new List<IRenderable>
                {
                    new Markup($"[bold]{Markup.Escape(j.ForemanName)}[/] {Markup.Escape(Truncate(j.Task, 40))}"),
                };

                if (!string.IsNullOrWhiteSpace(j.Summary))
                {
                    lines.Add(new Markup($"[grey]{Markup.Escape(Truncate(j.Summary, 60))}[/]"));
                }

                return (IRenderable)new Rows(lines);
            }));

        return new Panel(body)
            .Header($"[bold {color}]{title} ({jobs.Count})[/]")
            .Border(BoxBorder.Rounded);
    }

    /// <summary>
    /// The memory tab is a mode, not a pane: the browser is modal (it takes
    /// the whole screen, like /view and the wizards), so this only shows
    /// while the Boss is between browses.
    /// </summary>
    private static IRenderable BuildMemoryHint() =>
        new Markup("[grey]/memory opens the crew's vault folders -- pick a folder, walk down to a note, and it renders full width.[/]");

    /// <summary>
    /// Who is working right now. Rebuilt from the registries on every render:
    /// no Live region or timer needed, since Render already redraws once per
    /// event batch.
    /// </summary>
    private static IRenderable BuildMonitor(ForemanDirectory foremen, JobsiteDirectory jobsites, JobRegistry jobs)
    {
        var rows = MonitorRows(foremen, jobsites, jobs, DateTimeOffset.UtcNow);

        var table = new Table().Border(TableBorder.Rounded).Expand();
        table.AddColumn("who");
        table.AddColumn("kind");
        table.AddColumn("state");
        table.AddColumn("task");
        table.AddColumn("started");
        table.AddColumn("elapsed");

        foreach (var row in rows)
        {
            var stateMarkup = row.State switch
            {
                "working" => "[yellow]working[/]",
                "parked" => "[magenta]parked[/]",
                _ => "[grey]idle[/]",
            };

            var who = row.Jobsite is null
                ? Markup.Escape(row.Who)
                : $"{Markup.Escape(row.Who)} [grey]@ {Markup.Escape(row.Jobsite)}[/]";

            table.AddRow(
                new Markup($"[bold]{who}[/]"),
                new Markup($"[grey]{Markup.Escape(row.Kind)}[/]"),
                new Markup(stateMarkup),
                new Markup(row.Task is null ? "[grey]-[/]" : Markup.Escape(Truncate(row.Task, 40))),
                new Markup(row.StartedAt is { } startedAt
                    ? Markup.Escape(startedAt.ToLocalTime().ToString("HH:mm"))
                    : "[grey]-[/]"),
                new Markup(row.Elapsed is { } elapsed ? Markup.Escape(FormatElapsed(elapsed)) : "[grey]-[/]"));
        }

        return table;
    }

    private static string FormatElapsed(TimeSpan elapsed)
    {
        var clamped = elapsed < TimeSpan.Zero ? TimeSpan.Zero : elapsed;
        return clamped.TotalHours >= 1
            ? $"{(int)clamped.TotalHours}h{clamped.Minutes:00}m"
            : $"{(int)clamped.TotalMinutes}m";
    }

    /// <summary>
    /// One monitor line. <paramref name="Elapsed"/> is already net of parked
    /// time: actual worked time, not wall-clock since dispatch.
    /// </summary>
    internal sealed record MonitorRow(
        string Who,
        string Kind,          // "GC" | "Foreman" | "Worker"
        string State,         // "working" | "parked" | "idle"
        string? Task,
        DateTimeOffset? StartedAt,
        TimeSpan? Elapsed,
        string? Jobsite);

    /// <summary>
    /// One row per hired crew member, plus one per Worker with a job still in
    /// flight. Workers are transient: they appear when spawned and vanish
    /// when their job goes terminal.
    /// </summary>
    internal static IReadOnlyList<MonitorRow> MonitorRows(
        ForemanDirectory foremen, JobsiteDirectory jobsites, JobRegistry jobs, DateTimeOffset now) =>
        BuildMonitorRows(foremen, jobsites, jobs.GetAllJobs(), jobs.IsForemanBusy, jobs.IsForemanParked, now);

    /// <summary>
    /// The same view over a plain job list, for states a real JobRegistry only
    /// reaches through a live agent. The two predicates restate
    /// JobRegistry.IsForemanBusy/IsForemanParked over that list, through the
    /// same ownership rule <see cref="DriveCommands.BelongsTo"/> uses.
    /// </summary>
    internal static IReadOnlyList<MonitorRow> MonitorRows(
        ForemanDirectory foremen, JobsiteDirectory jobsites, IReadOnlyCollection<JobRecord> all, DateTimeOffset now) =>
        BuildMonitorRows(
            foremen,
            jobsites,
            all,
            name => all.Any(j => j.Status is JobStatus.Pending or JobStatus.Running &&
                                 DriveCommands.BelongsTo(name, j.ForemanName)),
            name => all.Any(j => j.Status is JobStatus.Parked && DriveCommands.BelongsTo(name, j.ForemanName)),
            now);

    private static IReadOnlyList<MonitorRow> BuildMonitorRows(
        ForemanDirectory foremen,
        JobsiteDirectory jobsites,
        IReadOnlyCollection<JobRecord> all,
        Func<string, bool> isBusy,
        Func<string, bool> isParked,
        DateTimeOffset now)
    {
        var rows = new List<MonitorRow>();

        // GC first, then Foremen in roster order. OrderBy is stable, so
        // roster order survives inside each group.
        var crew = foremen.All().OrderBy(f => f.Role == CrewRole.GC ? 0 : 1).ToList();

        foreach (var member in crew)
        {
            // State counts a Worker's job against its parent (a Foreman whose
            // only in-flight work is a Worker's is not free). Task/StartedAt
            // deliberately do NOT follow a Worker's job up to the parent: that
            // Worker gets its own row further down.
            var own = all
                .Where(j => j.Status is JobStatus.Pending or JobStatus.Running &&
                            j.ForemanName.Equals(member.Name, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(j => j.CreatedAt)
                .FirstOrDefault();

            rows.Add(new MonitorRow(
                member.Name,
                member.Role == CrewRole.GC ? "GC" : "Foreman",
                StateOf(isBusy(member.Name), isParked(member.Name)),
                own?.Task,
                own?.StartedAt,
                ElapsedOf(own, now),
                JobsiteNameFor(member.JobsiteName, jobsites)));
        }

        foreach (var job in all.Where(j => j.ForemanName.Contains(WorkerMarker, StringComparison.OrdinalIgnoreCase) &&
                                          j.Status is JobStatus.Pending or JobStatus.Running))
        {
            var parentName = job.ForemanName[..job.ForemanName.IndexOf(WorkerMarker, StringComparison.OrdinalIgnoreCase)];

            rows.Add(new MonitorRow(
                job.ForemanName,
                "Worker",
                "working",
                job.Task,
                job.StartedAt,
                ElapsedOf(job, now),
                JobsiteNameFor(foremen.Find(parentName)?.JobsiteName, jobsites)));
        }

        return rows;
    }

    /// <summary>The Worker label convention JobRegistry.StartWorkerJob mints: <c>&lt;Parent&gt;/worker-&lt;shortId&gt;</c>.</summary>
    private const string WorkerMarker = "/worker-";

    /// <summary>Text form of the same three-state rule <see cref="StatusBadge"/> renders. Busy wins over parked.</summary>
    private static string StateOf(bool busy, bool parked) =>
        busy ? "working" : parked ? "parked" : "idle";

    /// <summary>
    /// Actual worked time: wall clock since dispatch, less time spent parked
    /// waiting on the Boss. Null until the job starts, since queue time is
    /// never charged as work.
    /// </summary>
    private static TimeSpan? ElapsedOf(JobRecord? job, DateTimeOffset now) =>
        job?.StartedAt is { } startedAt ? now - startedAt - job.ParkedDuration : null;

    /// <summary>
    /// The jobsite's canonical name, so a Foreman configured against "xinfra"
    /// reports the roster's "XINFRA". An unrecognized name is still shown so
    /// the mismatch is visible.
    /// </summary>
    private static string? JobsiteNameFor(string? jobsiteName, JobsiteDirectory jobsites) =>
        string.IsNullOrWhiteSpace(jobsiteName) ? null : jobsites.Find(jobsiteName)?.Name ?? jobsiteName;

    /// <summary>What an unrecognized slash command lands on.</summary>
    private static IRenderable BuildStub(string label) =>
        new Markup($"[grey]'/{Markup.Escape(label)}' isn't a command -- /help lists the ones that are.[/]");

    private static string Truncate(string text, int max)
    {
        var oneLine = text.ReplaceLineEndings(" ");
        return oneLine.Length > max ? oneLine[..max] + "..." : oneLine;
    }
}
