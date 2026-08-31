using ConstructionCrew.Core.Abstractions;
using ConstructionCrew.Core.Models;

namespace ConstructionCrew.Providers;

/// <summary>
/// Shared runtime for GC and Foreman alike: on the first turn, prepend the
/// configured instructions file (AGENTS.md-style) to the message; later turns
/// continue the same CLI conversation. GC and a one-shot Foreman job both go
/// through this; GC just lives longer and gets more turns.
/// </summary>
public sealed class LocalCliAgent : ILocalCliAgent
{
    private readonly ForemanConfig _config;
    private readonly ICliToolProvider _provider;
    private readonly ICliProcessRunner _runner;
    private bool _hasSentFirstMessage;

    public LocalCliAgent(ForemanConfig config, ICliToolProvider provider, ICliProcessRunner runner)
    {
        _config = config;
        _provider = provider;
        _runner = runner;
    }

    public string Name => _config.Name;

    /// <summary>
    /// The last session id this agent's engine reported, or null if it has
    /// reported none. Sticky: a turn whose envelope carried no id (a crashed
    /// CLI, a plain-text provider) leaves the previously known id in place
    /// rather than blanking a conversation that is still perfectly resumable.
    /// </summary>
    public string? SessionId { get; private set; }

    public async Task<CliRunResult> SendAsync(string message, CancellationToken cancellationToken)
    {
        var prompt = _hasSentFirstMessage ? message : ComposeInitialPrompt(message);

        var request = new CliTaskRequest(
            Prompt: prompt,
            WorkingDirectory: _config.WorkingDirectory,
            ProviderOptions: _config.ProviderOptions,
            ContinuePreviousConversation: _hasSentFirstMessage,
            AddDirs: _config.AddDirs);

        var invocation = _provider.BuildInvocation(request);
        var result = await _runner.RunAsync(invocation, cancellationToken);
        _hasSentFirstMessage = true;

        // The provider gets the last word on its own output shape: this is where
        // a structured-output run becomes CliRunResult.Usage. Default is
        // identity, so other providers are unaffected.
        var processed = _provider.PostProcess(request, result);

        // No provider-name check and no second JSON parse: whichever provider
        // knows how to find its own session id has already put it on CliUsage.
        if (processed.Usage?.SessionId is { Length: > 0 } sessionId)
        {
            SessionId = sessionId;
        }

        return processed;
    }

    private string ComposeInitialPrompt(string message)
    {
        if (!File.Exists(_config.InstructionsFilePath))
        {
            return message;
        }

        var instructions = File.ReadAllText(_config.InstructionsFilePath);
        return string.IsNullOrWhiteSpace(instructions)
            ? message
            : $"{instructions}\n\n---\n\n{message}";
    }
}
