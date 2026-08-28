using System.Threading.Channels;
using ConstructionCrew.Core.Abstractions;
using ConstructionCrew.Core.Models;

namespace ConstructionCrew.Core.Runtime;

/// <summary>Default in-memory IJobStatusSink. Publish never blocks the dispatcher.</summary>
public sealed class JobStatusSink : IJobStatusSink
{
    private readonly Channel<JobRecord> _channel = Channel.CreateUnbounded<JobRecord>();

    public ChannelReader<JobRecord> Reader => _channel.Reader;

    public void Publish(JobRecord job) => _channel.Writer.TryWrite(job);
}
