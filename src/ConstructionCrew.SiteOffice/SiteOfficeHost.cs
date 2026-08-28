using ConstructionCrew.Core.Abstractions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace ConstructionCrew.SiteOffice;

/// <summary>
/// Hosts the Site Office MCP server over HTTP. GC and every Foreman are
/// independent concurrent CLI processes, not one stdio pipe each -- HTTP is
/// what lets them all reach the same control plane at once.
/// </summary>
public sealed class SiteOfficeHost : IAsyncDisposable
{
    private readonly WebApplication _app;

    private SiteOfficeHost(WebApplication app)
    {
        _app = app;
    }

    public Uri BaseAddress => new(_app.Urls.First());

    public static async Task<SiteOfficeHost> StartAsync(JobRegistry jobRegistry, IForemanDirectory foremen, IJobsiteDirectory jobsites, int port, CancellationToken cancellationToken)
    {
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.Logging.SetMinimumLevel(LogLevel.Warning);
        builder.WebHost.UseUrls($"http://127.0.0.1:{port}");

        builder.Services.AddSingleton(jobRegistry);
        builder.Services.AddSingleton(foremen);
        builder.Services.AddSingleton(jobsites);
        builder.Services.AddMcpServer()
            .WithHttpTransport()
            .WithToolsFromAssembly();

        var app = builder.Build();
        app.MapMcp("/mcp");

        await app.StartAsync(cancellationToken);

        return new SiteOfficeHost(app);
    }

    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
    }
}
