using System.Text.Json;
using ConstructionCrew.Core.Abstractions;
using ConstructionCrew.Core.Models;

namespace ConstructionCrew.Config;

/// <summary>
/// Appends one JSON line per JobRegistry.Transition call to state/jobs.jsonl.
/// Append-only, machine-local crash recovery only -- nothing reads it back as a
/// resume source of truth.
///
/// A single fixed lock, deliberately not RunLogWriter's keyed dictionary: this
/// writer targets exactly one path, resolved once at construction, for the whole
/// lifetime of the process. There is no "which file" question to key a lock by, so
/// one lock guarding the one file that could ever contend is both correct and
/// strictly simpler.
/// </summary>
public sealed class JobsLogWriter : IJobsLogWriter
{
    private readonly string _path;
    private readonly object _lock = new();

    public JobsLogWriter(string path)
    {
        _path = path;
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }
    }

    public void Append(JobRecord job)
    {
        var line = JsonSerializer.Serialize(job);
        lock (_lock)
        {
            File.AppendAllText(_path, line + Environment.NewLine);
        }
    }
}
