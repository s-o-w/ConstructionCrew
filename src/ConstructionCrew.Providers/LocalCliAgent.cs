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
        // an opt-in structured-output run becomes CliRunResult.Usage. Default is
        // identity, so other providers are unaffected.
        return _provider.PostProcess(request, result);
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
