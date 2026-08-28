using ConstructionCrew.App.Tui;
using ConstructionCrew.Config;
using ConstructionCrew.Core.Abstractions;
using ConstructionCrew.Core.Models;
using ConstructionCrew.Core.Runtime;
using ConstructionCrew.Providers;
using ConstructionCrew.SiteOffice;
using Spectre.Console;

var repoRoot = RepoPaths.FindRepoRoot(AppContext.BaseDirectory);
var settings = AppSettings.ForRepoRoot(repoRoot);

AnsiConsole.Write(new Rule("[bold yellow]ConstructionCrew[/]").LeftJustified());

IReadOnlyList<ForemanConfig> foremenSeed;
try
{
    foremenSeed = new ForemanConfigLoader().LoadFromFile(settings.ForemenConfigPath, repoRoot);
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

ICliToolProvider[] providers = [new ClaudeCodeProvider(), new GeminiProvider()];
var availableProviderIds = providers.Select(p => p.ProviderId).Where(id => id != "gemini").ToList(); // gemini isn't wired yet

var runner = new CliProcessRunner();
var agentFactory = new LocalCliAgentFactory(providers, runner);
var statusSink = new JobStatusSink();
var jobRegistry = new JobRegistry(foremanDirectory, agentFactory, statusSink);

Directory.CreateDirectory(settings.StateDirectory);

using var cts = new CancellationTokenSource();
var siteOffice = await SiteOfficeHost.StartAsync(jobRegistry, foremanDirectory, jobsiteDirectory, settings.SiteOfficePort, cts.Token);

// Every Foreman needs the Site Office's MCP config to call list_foremen/dispatch_task/
// spawn_worker/ask_foreman -- not just GC. Stamp it onto everyone hired so far;
// HireWizard does the same for anyone hired mid-session.
string? mcpConfigPath = null;
if (gcConfig.Provider.Equals("claude", StringComparison.OrdinalIgnoreCase))
{
    mcpConfigPath = McpConfigWriter.WriteClaudeCodeConfig(settings.GeneratedConfigDirectory, siteOffice.BaseAddress);

    foreach (var foreman in foremanDirectory.All().ToList())
    {
        var merged = new Dictionary<string, string>(foreman.ProviderOptions) { ["mcpConfigPath"] = mcpConfigPath };
        foremanDirectory.Add(foreman with { ProviderOptions = merged });
    }

    gcConfig = foremanDirectory.Find(settings.GcForemanName)!;
}
else
{
    AnsiConsole.MarkupLine($"[yellow]GC provider '{gcConfig.Provider}' isn't wired to the Site Office yet -- only Claude Code's --mcp-config shape has been verified.[/]");
}

var gc = agentFactory.Create(gcConfig);

var state = new DashboardState { SiteOfficeAddress = siteOffice.BaseAddress.ToString() };

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
        AnsiConsole.MarkupLine("[grey]/chat  /tasks  /hire  /fire  /exit -- anything else is sent to the GC as a message.[/]");
        AnsiConsole.Markup("[grey]Press enter to continue...[/]");
        Console.ReadLine();
        continue;
    }

    if (command.Equals("/hire", StringComparison.OrdinalIgnoreCase))
    {
        AnsiConsole.Clear();
        HireWizard.Run(foremanDirectory, jobsiteDirectory, availableProviderIds, repoRoot, mcpConfigPath);
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
            result = await gc.SendAsync(input, cts.Token);
        });

    state.Transcript.Add(new TranscriptLine("GC", result.Succeeded ? result.StandardOutput : result.StandardError, IsError: !result.Succeeded));
}

await siteOffice.DisposeAsync();
return 0;

static bool IsExit(string input)
{
    var trimmed = input.Trim();
    return trimmed.Equals("exit", StringComparison.OrdinalIgnoreCase) ||
           trimmed.Equals("quit", StringComparison.OrdinalIgnoreCase) ||
           trimmed.Equals("/exit", StringComparison.OrdinalIgnoreCase);
}
