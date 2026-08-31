using System.Threading.Channels;
using ConstructionCrew.Core.Models;

namespace ConstructionCrew.Core.Abstractions;

/// <summary>
/// Where job status transitions go so the TUI can render them without being on
/// the hook for a dispatch call. Publish is fire-and-forget, non-blocking.
/// </summary>
public interface IJobStatusSink
{
    void Publish(JobRecord job);

    ChannelReader<JobRecord> Reader { get; }
}
