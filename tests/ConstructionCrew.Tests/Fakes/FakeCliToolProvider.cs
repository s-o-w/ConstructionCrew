using ConstructionCrew.Core.Abstractions;

namespace ConstructionCrew.Tests.Fakes;

/// <summary>Records the requests it was asked to build invocations for. Never touches CliWrap.</summary>
public sealed class FakeCliToolProvider : ICliToolProvider
{
    public string ProviderId { get; }
    public string ExecutableName { get; }
    public bool IsImplemented { get; }
    public List<CliTaskRequest> Requests { get; } = new();

    public FakeCliToolProvider(string providerId = "fake", string? executableName = null, bool isImplemented = true)
    {
        ProviderId = providerId;
        ExecutableName = executableName ?? providerId;
        IsImplemented = isImplemented;
    }

    /// <summary>
    /// What this provider's PostProcess hangs on the result, standing in for a
    /// real engine's accounting envelope. Null (the default) is the honest shape
    /// for a provider that reports nothing.
    /// </summary>
    public CliUsage? NextUsage { get; set; }

    public CliInvocation BuildInvocation(CliTaskRequest request)
    {
        Requests.Add(request);
        return new CliInvocation("fake-exe", [request.Prompt], request.WorkingDirectory);
    }

    public CliRunResult PostProcess(CliTaskRequest request, CliRunResult result) =>
        NextUsage is null ? result : result with { Usage = NextUsage };
}
