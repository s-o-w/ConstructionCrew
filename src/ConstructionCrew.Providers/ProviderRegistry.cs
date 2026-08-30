using System.Text.Json;
using System.Text.Json.Serialization;
using ConstructionCrew.Core.Abstractions;

namespace ConstructionCrew.Providers;

/// <summary>One provider's discovery outcome. Serialized as-is into state/tools.json.</summary>
public sealed record ProviderProbe(
    string ProviderId,
    string ExecutableName,
    bool Implemented,
    string? ResolvedPath)
{
    /// <summary>
    /// A provider is offerable only if it is implemented in code AND its binary
    /// resolves on PATH. Both halves matter: Gemini's binary is on PATH on at least
    /// one dev machine but its provider throws, and Claude Code is implemented but
    /// absent on a machine where it was never installed.
    /// </summary>
    [JsonIgnore]
    public bool Available => Implemented && ResolvedPath is not null;
}

/// <summary>The whole probe run, as cached to state/tools.json.</summary>
public sealed record ToolDiscoveryCache(DateTimeOffset ProbedAtUtc, IReadOnlyList<ProviderProbe> Tools);

/// <summary>
/// Answers "which CLI tools can this machine actually hire". Replaces the hardcoded
/// provider array Program.cs used to carry. Results are cached to state/tools.json so
/// the answer survives a restart; <see cref="Refresh"/> re-probes on demand (that is
/// what the /settings command calls).
/// </summary>
public sealed class ProviderRegistry
{
    private readonly IReadOnlyList<ICliToolProvider> _registered;
    private readonly Func<string, string?> _resolveOnPath;
    private readonly string? _cachePath;

    private IReadOnlyList<ProviderProbe>? _lastProbe;

    public ProviderRegistry(
        IEnumerable<ICliToolProvider> registered,
        Func<string, string?>? resolveOnPath = null,
        string? toolsCachePath = null)
    {
        _registered = registered.ToList();
        _resolveOnPath = resolveOnPath ?? ResolveOnPath;
        _cachePath = toolsCachePath;
    }

    /// <summary>
    /// Every provider that exists in code, implemented or not. The hire wizard must
    /// never be fed this list directly -- use <see cref="Available"/>.
    /// </summary>
    public IReadOnlyList<ICliToolProvider> Registered => _registered;

    /// <summary>
    /// Every provider ConstructionCrew ships. GeminiProvider is deliberately included:
    /// it reports IsImplemented == false, so the registry filters it without anyone
    /// hand-writing an id blocklist.
    /// </summary>
    public static IReadOnlyList<ICliToolProvider> DefaultProviders() =>
    [
        new ClaudeCodeProvider(),
        new CodexProvider(),
        new CopilotProvider(),
        new GeminiProvider(),
    ];

    public static ProviderRegistry Default(string stateDirectory) =>
        new(DefaultProviders(), resolveOnPath: null, toolsCachePath: Path.Combine(stateDirectory, "tools.json"));

    /// <summary>Providers usable right now. Probes once, then serves the memoized answer.</summary>
    public IReadOnlyList<ICliToolProvider> Available()
    {
        var probe = _lastProbe ?? Refresh();
        var availableIds = probe.Where(p => p.Available)
            .Select(p => p.ProviderId)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        return _registered.Where(p => availableIds.Contains(p.ProviderId)).ToList();
    }

    public IReadOnlyList<string> AvailableIds() => Available().Select(p => p.ProviderId).ToList();

    /// <summary>The most recent probe, probing first if none has run yet. Drives the /settings table.</summary>
    public IReadOnlyList<ProviderProbe> Probes() => _lastProbe ?? Refresh();

    /// <summary>Re-runs discovery from scratch and rewrites state/tools.json.</summary>
    public IReadOnlyList<ProviderProbe> Refresh()
    {
        var probes = _registered
            .Select(p => new ProviderProbe(
                p.ProviderId,
                p.ExecutableName,
                p.IsImplemented,
                // Skip the filesystem walk entirely for a provider that could not be
                // used even if found.
                p.IsImplemented ? _resolveOnPath(p.ExecutableName) : null))
            .ToList();

        _lastProbe = probes;
        WriteCache(probes);
        return probes;
    }

    private void WriteCache(IReadOnlyList<ProviderProbe> probes)
    {
        if (string.IsNullOrWhiteSpace(_cachePath))
        {
            return;
        }

        try
        {
            var directory = Path.GetDirectoryName(_cachePath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var cache = new ToolDiscoveryCache(DateTimeOffset.UtcNow, probes);
            File.WriteAllText(_cachePath, JsonSerializer.Serialize(cache, CacheJsonOptions));
        }
        catch (IOException)
        {
            // The cache is a convenience, never the source of truth -- Available()
            // always reflects the live probe. A read-only or contended state/ dir
            // must not take the app down.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    /// <summary>Reads a previously written cache, or null if there isn't a usable one.</summary>
    public static ToolDiscoveryCache? ReadCache(string toolsCachePath)
    {
        try
        {
            return File.Exists(toolsCachePath)
                ? JsonSerializer.Deserialize<ToolDiscoveryCache>(File.ReadAllText(toolsCachePath), CacheJsonOptions)
                : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    private static readonly JsonSerializerOptions CacheJsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    /// <summary>
    /// `which`-equivalent, without shelling out to `which`/`where` (which don't exist
    /// on every target and would cost a process per provider per probe). Honours
    /// PATHEXT on Windows so `claude.cmd` -- the shape an npm-installed CLI actually
    /// takes there -- is found.
    /// </summary>
    public static string? ResolveOnPath(string executable)
    {
        if (string.IsNullOrWhiteSpace(executable))
        {
            return null;
        }

        // An explicit path (absolute or relative) is used as given, not searched for.
        if (executable.Contains(Path.DirectorySeparatorChar) || executable.Contains(Path.AltDirectorySeparatorChar))
        {
            try
            {
                return File.Exists(executable) ? Path.GetFullPath(executable) : null;
            }
            catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException)
            {
                return null;
            }
        }

        var extensions = OperatingSystem.IsWindows()
            ? (Environment.GetEnvironmentVariable("PATHEXT") ?? ".EXE;.CMD;.BAT;.COM")
                .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            : [string.Empty];

        var path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;

        foreach (var rawDirectory in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var directory = rawDirectory.Trim().Trim('"');
            if (directory.Length == 0)
            {
                continue;
            }

            foreach (var extension in extensions)
            {
                string candidate;
                try
                {
                    candidate = Path.Combine(directory, executable + extension);
                }
                catch (ArgumentException)
                {
                    // A malformed PATH entry is skipped, not fatal.
                    break;
                }

                try
                {
                    if (File.Exists(candidate))
                    {
                        return candidate;
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                }
            }
        }

        return null;
    }
}
