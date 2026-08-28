using ConstructionCrew.Core.Abstractions;

namespace ConstructionCrew.Tests.Fakes;

/// <summary>Records the requests it was asked to build invocations for. Never touches CliWrap.</summary>
public sealed class FakeCliToolProvider : ICliToolProvider
{
    public string ProviderId { get; }
    public List<CliTaskRequest> Requests { get; } = new();

    public FakeCliToolProvider(string providerId = "fake")
    {
        ProviderId = providerId;
    }

    public CliInvocation BuildInvocation(CliTaskRequest request)
    {
        Requests.Add(request);
        return new CliInvocation("fake-exe", [request.Prompt], request.WorkingDirectory);
    }
}
