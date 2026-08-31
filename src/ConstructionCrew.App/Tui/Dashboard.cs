using ConstructionCrew.Config;
using ConstructionCrew.Core.Models;
using ConstructionCrew.HomeOffice;
using Spectre.Console;
using Spectre.Console.Rendering;

namespace ConstructionCrew.App.Tui;

/// <summary>
/// Renders the full-screen shell once per Boss turn: header, roster sidebar,
/// tab strip, and the active view's content. Not a Live-updating region --
/// each call clears and redraws, which is simple and matches a chat-driven
/// (not raw-keyboard-driven) interaction model.
/// </summary>
public static class Dashboard
{
    // Every tab here is live. "memory" and "monitor" were the last two
    // placeholders; "memory" is modal (the browser takes over the screen, like
    // /view and the wizards) and its tab is what shows the Boss is in it, while
    // "monitor" renders in place off the registries. "ask me"/"triggers"/
    // "activity" were removed outright: first-commit scaffolding with no
    // documented purpose behind any of them.
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

    public static void Render(ForemanDirectory foremen, JobsiteDirectory jobsites, JobRegistry jobs, DashboardState state)
    {
        ClearScreen();

        // Spectre.Console's root Layout always renders exactly ConsoleSize.Height
        // total lines, full stop -- confirmed by reading Layout.Render's source.
        // Any .Size() on children only controls how that fixed total is divided,
        // never the total itself, and leftover space still has to go somewhere
        // whether or not I want it to. So: don't fight it -- leave "body" unsized
        // (ratio-based) so it automatically absorbs whatever's left between
        // header and footer. That's also what makes this scale on resize for
        // free, since it's recomputed fresh from the real console size every
        // render call.
        var root = new Layout("root").SplitRows(
            new Layout("header").Size(3),
            new Layout("body"),
            new Layout("footer").Size(2));

        root["header"].Update(BuildHeader());

        var body = new Layout("body").SplitColumns(
            new Layout("sidebar").Size(26),
            new Layout("main"));

        body["sidebar"].Update(BuildSidebar(foremen, jobsites, jobs));
        body["main"].Update(BuildMain(foremen, jobsites, jobs, state));

        root["body"].Update(body);

        // footer's second line is deliberately left blank -- it's the one row
        // the layout is guaranteed to have printed as blank, which is where the
        // Boss prompt gets positioned next, so nothing after this ever needs an
        // extra line (i.e. never scrolls the pinned header out of view).
        root["footer"].Update(new Rows(
            new Markup(FooterFor(state.DrivenForeman)),
            Text.Empty));

        AnsiConsole.Write(root);
        PositionCursorOnPromptRow();
    }

    /// <summary>
    /// The one-line command hint under the board. Two states: the GC-level command
    /// list, and the reminder shown while the Boss is driving a Foreman directly.
    /// Named rather than inlined so a test can assert the command list without
    /// scraping a rendered layout.
    ///
    /// The placeholders are both &lt;Name&gt;, not &lt;Foreman&gt;: the line has to
    /// survive a narrow console, and /foreman had to be added to it.
    /// </summary>
    internal static string FooterFor(string? drivenForeman) =>
        drivenForeman is null
            ? "[grey]/tasks /monitor /memory /hire /fire /foreman <Name> /view <path> /chat /drive <Name> /settings /migrate /help /exit[/]"
            : $"[grey]driving [/][yellow]{Markup.Escape(drivenForeman)}[/][grey] -- /exit returns to GC[/]";

    private static void PositionCursorOnPromptRow()
    {
        if (Console.IsOutputRedirected)
        {
            return;
        }

        try
        {
            // Same value Spectre's own Layout.Render just used internally to
            // size "root" -- using anything else risks a 1-row mismatch between
            // where the layout actually left its blank last row and where this
            // moves the cursor to.
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
            // Raw ANSI clear-screen + cursor-home, written directly rather than
            // through System.Console.Clear()/AnsiConsole.Clear(). Those go
            // through Win32 console APIs that don't reliably work under a
            // pty-based terminal like mintty/Git Bash -- confirmed by testing:
            // Console.Clear() there didn't actually clear the screen, and
            // Console.WindowHeight reported values that didn't match what was
            // visible. Raw ANSI is what every other bit of this app's colored
            // output already goes through successfully, so it's the reliable
            // path here too.
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

    private static IRenderable BuildSidebar(ForemanDirectory foremen, JobsiteDirectory jobsites, JobRegistry jobs)
    {
        var rows = new List<IRenderable>();

        var gc = foremen.Find("GC");
        if (gc is not null)
        {
            rows.Add(new Markup($"[bold]GC[/]  {StatusBadge(jobs.IsForemanBusy("GC"), jobs.IsForemanParked("GC"))}"));
            rows.Add(new Markup($"[grey]{gc.Provider}[/]"));
            rows.Add(Text.Empty);
        }

        foreach (var foreman in foremen.All().Where(f => !f.Name.Equals("GC", StringComparison.OrdinalIgnoreCase)))
        {
            rows.Add(BuildForemanEntry(foreman, jobsites, jobs));
            rows.Add(Text.Empty);
        }

        rows.Add(new Markup("[grey]/hire to add, /fire to remove[/]"));

        return new Panel(new Rows(rows))
            .Header("[bold]site roster[/]")
            .Border(BoxBorder.Rounded)
            .Expand();
    }

    private static IRenderable BuildForemanEntry(ForemanConfig foreman, JobsiteDirectory jobsites, JobRegistry jobs)
    {
        var jobsiteSuffix = string.IsNullOrWhiteSpace(foreman.JobsiteName) ? "" : $" @ {foreman.JobsiteName}";
        var entry = new Rows(
            new Markup($"[bold]{Markup.Escape(foreman.Name)}[/]  {StatusBadge(jobs.IsForemanBusy(foreman.Name), jobs.IsForemanParked(foreman.Name))}"),
            new Markup($"[grey]{Markup.Escape(foreman.Provider)}{Markup.Escape(jobsiteSuffix)}[/]"));

        // Every Foreman is strictly assigned to one Jobsite (except GC, handled
        // separately above) -- give it that jobsite's color as a small border,
        // so at a glance the roster shows which site each Foreman belongs to.
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
    /// Three states, not two. A parked Foreman is NOT busy (IsForemanBusy is false
    /// by design -- it can still take a sitrep or a redirect) but it is not free
    /// either: it is blocked on the Boss, and rendering it as "idle" would hide the
    /// one thing the Boss has to act on.
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
    /// In drive mode the chat pane gets a passive column beside it: a read-only
    /// <c>git status</c>/<c>git log</c> of the worktree that Foreman (or one of its
    /// Workers) is in. A Grid, not a nested Layout -- Layout always renders the
    /// full console height, which is right for the shell and wrong for a cell
    /// inside it.
    /// </summary>
    private static IRenderable BuildChatPane(DashboardState state)
    {
        var chat = BuildChat(state);
        if (state.DrivenForeman is null)
        {
            return chat;
        }

        var grid = new Grid().Expand();
        grid.AddColumn(new GridColumn());
        grid.AddColumn(new GridColumn().Width(38).NoWrap());
        grid.AddRow(chat, BuildPassiveColumn(state));
        return grid;
    }

    private static IRenderable BuildPassiveColumn(DashboardState state)
    {
        var rows = new List<IRenderable>();
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
                : snapshot.RecentCommits.Select(c => (IRenderable)new Markup($"[grey]{Markup.Escape(Truncate(c, 34))}[/]")));
        }

        return new Panel(new Rows(rows))
            .Header($"[bold]{Markup.Escape(state.DrivenForeman ?? "")}[/]")
            .Border(BoxBorder.Rounded)
            .Expand();
    }

    private static IRenderable BuildChat(DashboardState state)
    {
        var transcript = state.ActiveTranscript;

        if (transcript.Count == 0)
        {
            return new Markup(state.DrivenForeman is null
                ? "[grey]Say something to the GC to get started.[/]"
                : $"[grey]Driving {Markup.Escape(state.DrivenForeman)} -- anything you type goes to them. /exit returns to GC.[/]");
        }

        var rows = transcript
            .TakeLast(10)
            .Select(line =>
            {
                var speakerStyle = line.Speaker == "Boss" ? "cyan" : line.IsError ? "red" : "green";
                return (IRenderable)new Markup($"[bold {speakerStyle}]{line.Speaker}:[/] {Markup.Escape(Truncate(line.Text, 400))}");
            })
            .ToList();

        return new Rows(rows);
    }

    private static IRenderable BuildTasks(JobRegistry jobs) =>
        new Columns(TaskColumns(jobs.GetAllJobs())
            .Select(c => BuildTaskColumn(c.Title, c.Color, c.Jobs))
            .ToArray());

    /// <summary>
    /// The task board's columns, in order. Split out from the rendering so the
    /// membership rules (a Parked job is its own column, never "doing") can be
    /// asserted directly.
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
            : (IRenderable)new Rows(jobs.Select(j => (IRenderable)new Markup($"[bold]{Markup.Escape(j.ForemanName)}[/] {Markup.Escape(Truncate(j.Task, 40))}")));

        return new Panel(body)
            .Header($"[bold {color}]{title} ({jobs.Count})[/]")
            .Border(BoxBorder.Rounded);
    }

    /// <summary>
    /// The memory tab is a mode, not a pane: the browser itself is modal (it
    /// takes the whole screen, like /view and the wizards), so the tab only ever
    /// shows while the Boss is between browses.
    /// </summary>
    private static IRenderable BuildMemoryHint() =>
        new Markup("[grey]/memory opens the crew's vault folders -- pick a folder, walk down to a note, and it renders full width.[/]");

    /// <summary>
    /// Who is working right now. Rebuilt from the registries on every render --
    /// no Live region and no timer, because Render already redraws once per event
    /// batch and a JobRegistry transition is one of those events.
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
    /// One monitor line. <paramref name="Elapsed"/> is already net of parked time,
    /// so it is actual worked time, not wall-clock since dispatch.
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
    /// flight. Workers are transient by design -- they appear when spawned and
    /// vanish when their job goes terminal, which is the whole point of the view.
    /// </summary>
    internal static IReadOnlyList<MonitorRow> MonitorRows(
        ForemanDirectory foremen, JobsiteDirectory jobsites, JobRegistry jobs, DateTimeOffset now) =>
        BuildMonitorRows(foremen, jobsites, jobs.GetAllJobs(), jobs.IsForemanBusy, jobs.IsForemanParked, now);

    /// <summary>
    /// The same view over a plain job list, for states a real JobRegistry only
    /// ever reaches through a live agent (Parked, and a resumed job's
    /// ParkedDuration). The two predicates are JobRegistry.IsForemanBusy and
    /// IsForemanParked restated over the same list, through the ownership rule
    /// <see cref="DriveCommands.BelongsTo"/> already mirrors -- not a second rule.
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

        // GC first, then the Foremen in roster order. OrderBy is stable, so the
        // roster's own order survives inside each group.
        var crew = foremen.All().OrderBy(f => f.Role == CrewRole.GC ? 0 : 1).ToList();

        foreach (var member in crew)
        {
            // State is the registry's rule verbatim, which counts a Worker's job
            // against its parent -- a Foreman whose only in-flight work is a
            // Worker's is genuinely not free. Task/StartedAt deliberately do NOT
            // follow a Worker's job up to the parent: that Worker has a row of its
            // own further down, and repeating its task on the parent's line reads
            // as two pieces of work when there is one.
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

    /// <summary>
    /// The three-state rule <see cref="StatusBadge"/> already renders, as text.
    /// Busy wins over parked, exactly as it does there.
    /// </summary>
    private static string StateOf(bool busy, bool parked) =>
        busy ? "working" : parked ? "parked" : "idle";

    /// <summary>
    /// Actual worked time: wall clock since dispatch began, less however long the
    /// job has sat parked waiting on the Boss. Null until the job actually starts,
    /// because queue time is visible elsewhere and is never charged as work.
    /// </summary>
    private static TimeSpan? ElapsedOf(JobRecord? job, DateTimeOffset now) =>
        job?.StartedAt is { } startedAt ? now - startedAt - job.ParkedDuration : null;

    /// <summary>
    /// The jobsite's canonical name, so a Foreman configured against "xinfra"
    /// reports the roster's "XINFRA". An unconfigured name is still shown -- it is
    /// what the Foreman's own config says, and hiding it would hide the mismatch.
    /// </summary>
    private static string? JobsiteNameFor(string? jobsiteName, JobsiteDirectory jobsites) =>
        string.IsNullOrWhiteSpace(jobsiteName) ? null : jobsites.Find(jobsiteName)?.Name ?? jobsiteName;

    /// <summary>
    /// What an unrecognized slash command lands on. Every tab in the strip is
    /// built now, so this is no longer a "later phase" placeholder -- it is the
    /// answer to a command that does not exist.
    /// </summary>
    private static IRenderable BuildStub(string label) =>
        new Markup($"[grey]'/{Markup.Escape(label)}' isn't a command -- /help lists the ones that are.[/]");

    private static string Truncate(string text, int max)
    {
        var oneLine = text.ReplaceLineEndings(" ");
        return oneLine.Length > max ? oneLine[..max] + "..." : oneLine;
    }
}
