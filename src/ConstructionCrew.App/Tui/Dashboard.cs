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
    private static readonly (string Id, string Label)[] Tabs =
    [
        ("chat", "chat"),
        ("tasks", "tasks"),
        ("hire", "hire"),
        ("ask me", "ask me"),
        ("memory", "memory"),
        ("triggers", "triggers"),
        ("monitor", "monitor"),
        ("activity", "activity"),
    ];

    private static readonly HashSet<string> LiveTabs = new(StringComparer.OrdinalIgnoreCase) { "chat", "tasks", "hire" };

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
        body["main"].Update(BuildMain(jobs, state));

        root["body"].Update(body);

        // footer's second line is deliberately left blank -- it's the one row
        // the layout is guaranteed to have printed as blank, which is where the
        // Boss prompt gets positioned next, so nothing after this ever needs an
        // extra line (i.e. never scrolls the pinned header out of view).
        root["footer"].Update(new Rows(
            new Markup(state.DrivenForeman is null
                ? "[grey]/tasks /hire /fire /chat /drive <Foreman> /settings /help /exit[/]"
                : $"[grey]driving [/][yellow]{Markup.Escape(state.DrivenForeman)}[/][grey] -- /exit returns to GC[/]"),
            Text.Empty));

        AnsiConsole.Write(root);
        PositionCursorOnPromptRow();
    }

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

    private static IRenderable BuildMain(JobRegistry jobs, DashboardState state)
    {
        var tabStrip = new Markup(string.Join("   ", Tabs.Select(t => RenderTab(t, state))));

        IRenderable content = state.View switch
        {
            TuiView.Chat => BuildChatPane(state),
            TuiView.Tasks => BuildTasks(jobs),
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

    private static IRenderable BuildStub(string label) =>
        new Markup($"[grey]'{Markup.Escape(label)}' isn't built yet -- coming in a later phase.[/]");

    private static string Truncate(string text, int max)
    {
        var oneLine = text.ReplaceLineEndings(" ");
        return oneLine.Length > max ? oneLine[..max] + "..." : oneLine;
    }
}
