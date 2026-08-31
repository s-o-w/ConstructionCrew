using ConstructionCrew.Core.Models;

namespace ConstructionCrew.Core.Abstractions;

/// <summary>
/// Appends one entry per completed unit of work to
/// <c>&lt;plansFolder&gt;/RUN-LOG.md</c>. Lives in Core so HomeOffice can depend
/// on it without referencing Config, where the implementation lives
/// (Architecture §3.7).
///
/// Called from JobRegistry.Transition on a transition into Completed/Failed, and
/// only for a job carrying an ActiveWorkorder: an ad-hoc dispatch has no Plans
/// folder to log against, and logs nothing.
/// </summary>
public interface IRunLogWriter
{
    void Append(string plansFolder, JobRecord job);
}
