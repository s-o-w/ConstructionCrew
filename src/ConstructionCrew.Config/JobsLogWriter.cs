using System.Text.Json;
using ConstructionCrew.Core.Abstractions;
using ConstructionCrew.Core.Models;

namespace ConstructionCrew.Config;

/// <summary>
/// Appends one JSON line per JobRegistry.Transition call to state/jobs.jsonl.
/// Crash-recovery only: nothing reads it back as a resume source of truth.
///
/// Uses one fixed lock, not RunLogWriter's keyed dictionary: this writer
/// targets exactly one path for the process lifetime, so there is no "which
/// file" to key a lock by.
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
