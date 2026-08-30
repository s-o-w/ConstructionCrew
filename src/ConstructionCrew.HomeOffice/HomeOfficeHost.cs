using ConstructionCrew.Core.Abstractions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace ConstructionCrew.HomeOffice;

/// <summary>
/// Hosts the Home Office MCP server over HTTP. GC and every Foreman are
/// independent concurrent CLI processes, not one stdio pipe each -- HTTP is
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
    /// <paramref name="vaultGraph"/> is always the last parameter before
    /// <paramref name="port"/>; every later feature's parameter inserts ahead of
    /// it. Cross-project implementations arrive already constructed (Program.cs
    /// is the only place allowed to new them) and are registered as instances,
    /// never as types -- HomeOffice references only Core.
    /// </summary>
    public static async Task<HomeOfficeHost> StartAsync(
        JobRegistry jobRegistry,
        IForemanDirectory foremen,
        IJobsiteDirectory jobsites,
        HomeOfficeVaultOptions vaultOptions,
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
