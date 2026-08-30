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

    public CliInvocation BuildInvocation(CliTaskRequest request)
    {
        Requests.Add(request);
        return new CliInvocation("fake-exe", [request.Prompt], request.WorkingDirectory);
    }
}
