using ConstructionCrew.Core.Abstractions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace ConstructionCrew.HomeOffice;

/// <summary>
/// Hosts the Home Office MCP server over HTTP. GC and every Foreman are
/// independent concurrent CLI processes, not one stdio pipe each: HTTP is
/// what lets them all reach the same control plane at once.
/// </summary>
public sealed class HomeOfficeHost : IAsyncDisposable
{
    private readonly WebApplication _app;

    private HomeOfficeHost(WebApplication app)
    {
        _app = app;
    }

    public Uri BaseAddress => new(_app.Urls.First());

    /// <summary>
    /// Cross-project implementations arrive already constructed (only Program.cs
    /// news them) and register as instances, never as types, because HomeOffice
    /// references only Core. <paramref name="vaultGraph"/> stays the last
    /// parameter before <paramref name="port"/>; later features insert ahead of it.
    /// </summary>
    public static async Task<HomeOfficeHost> StartAsync(
        JobRegistry jobRegistry,
        IForemanDirectory foremen,
        IJobsiteDirectory jobsites,
        HomeOfficeVaultOptions vaultOptions,
        IWorkorderReader workorderReader,
        IWorktreeManager worktreeManager,
        ISitrepWriter sitrepWriter,
        IVaultGraph vaultGraph,
        int port,
        CancellationToken cancellationToken)
    {
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.Logging.SetMinimumLevel(LogLevel.Warning);
        builder.WebHost.UseUrls($"http://127.0.0.1:{port}");

        builder.Services.AddSingleton(jobRegistry);
        builder.Services.AddSingleton(foremen);
        builder.Services.AddSingleton(jobsites);
        builder.Services.AddSingleton(vaultOptions);
        // Registered as the INSTANCE, never AddSingleton<WorkorderReader>(): naming
        // the concrete type would force a ProjectReference to Config this project
        // must not have.
        builder.Services.AddSingleton(workorderReader);
        // Same reasoning: WorktreeManager lives in ConstructionCrew.Git.
        builder.Services.AddSingleton(worktreeManager);
        // Same reasoning: SitrepWriter lives in Config.
        builder.Services.AddSingleton(sitrepWriter);
        builder.Services.AddSingleton(vaultGraph);
        builder.Services.AddMcpServer()
            .WithHttpTransport()
            .WithToolsFromAssembly();

        var app = builder.Build();
        app.MapMcp("/mcp");

        await app.StartAsync(cancellationToken);

        return new HomeOfficeHost(app);
    }

    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
    }
}
