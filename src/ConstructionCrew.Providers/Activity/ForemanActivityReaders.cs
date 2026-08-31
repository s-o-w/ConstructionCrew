namespace ConstructionCrew.Providers.Activity;

/// <summary>
/// Which reader, if any, understands a given engine's transcripts.
///
/// <para>
/// A pure lookup with no I/O, so the TUI can answer "can I even watch this
/// one?" before it sets a watch that would only ever render blank. Copilot is
/// the deliberate null: it keeps its state in a SQLite <c>data.db</c>, not a
/// flat JSONL file, so it is a different mechanism rather than a missing case.
/// </para>
/// </summary>
public sealed class ForemanActivityReaders
{
    private readonly IReadOnlyDictionary<string, IForemanActivityReader> _byProvider;

    public ForemanActivityReaders(IEnumerable<IForemanActivityReader> readers)
    {
        _byProvider = readers.ToDictionary(r => r.ProviderId, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>The readers shipped by default: the engines whose on-disk transcript shape has been confirmed against a real session.</summary>
    public static ForemanActivityReaders Default() =>
        new([new ClaudeActivityReader(), new CodexActivityReader()]);

    /// <summary>Null for an engine with no transcript reader, which the caller must report rather than silently watch nothing.</summary>
    public IForemanActivityReader? For(string? providerId) =>
        string.IsNullOrWhiteSpace(providerId) ? null : _byProvider.GetValueOrDefault(providerId);
}
