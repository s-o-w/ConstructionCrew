using ConstructionCrew.App.Tui;
using ConstructionCrew.Core.Abstractions;
using ConstructionCrew.Core.Models;
using ConstructionCrew.Core.Runtime;
using ConstructionCrew.HomeOffice;
using ConstructionCrew.Providers;
using ConstructionCrew.Tests.Fakes;

namespace ConstructionCrew.Tests.AppTests;

/// <summary>
/// Phase 8a end to end, minus the console: a real JobRegistry, a real
/// JobStatusSink, and the exact draining logic the Boss loop runs. The unit tests
/// prove the pending set's rules; this proves the seam -- that
/// <c>StartJob</c> -> transitions on IJobStatusSink -> a transcript line is
/// actually wired, using the same public surface the loop has.
/// </summary>
public class BossTurnCompletionTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    private sealed class FakeForemanDirectory : IForemanDirectory
    {
        private readonly Dictionary<string, ForemanConfig> _byName;

        public FakeForemanDirectory(params ForemanConfig[] foremen) =>
            _byName = foremen.ToDictionary(f => f.Name, StringComparer.OrdinalIgnoreCase);

        public ForemanConfig? Find(string name) => _byName.GetValueOrDefault(name);

        public IReadOnlyCollection<ForemanConfig> All() => _byName.Values;
    }

    private static ForemanConfig Config(string name, CrewRole role) =>
        new(name, role, "fake", "dir", "instructions.md", new Dictionary<string, string>());

    private static JobRegistry BuildRegistry(IForemanDirectory foremen, IJobStatusSink sink, ICliProcessRunner runner)
    {
        var factory = new LocalCliAgentFactory([new FakeCliToolProvider("fake")], runner);
        return new JobRegistry(
            foremen,
            new FakeJobsiteDirectory(),
            factory,
            sink,
            // The one shared LiveAgentRegistry: the Boss loop dispatches through
            // JobRegistry precisely so GC's conversation is never forked.
            new LiveAgentRegistry(factory),
            "GC",
            new FakeWorktreeManager(),
            new JobRegistryRuntimeOptions(Path.Combine(Path.GetTempPath(), "cc-test-state")));
    }

    /// <summary>
    /// The loop's drain step, verbatim: read a record, offer it to the pending set,
    /// append whatever comes back. Stops once the transcript has grown.
    /// </summary>
    private static async Task DrainUntilAnnouncedAsync(
        JobStatusSink sink, PendingBossTurns pending, DashboardState state)
    {
        using var cts = new CancellationTokenSource(Timeout);

        while (true)
        {
            var record = await sink.Reader.ReadAsync(cts.Token);
            if (pending.TryTakeCompletion(record, out var speaker, out var line))
            {
                state.TranscriptFor(speaker).Add(line);
                return;
            }
        }
    }

    [Fact]
    public async Task BossTurnToGc_LandsInTheGcTranscriptWhenTheJobCompletes()
    {
        var sink = new JobStatusSink();
        var registry = BuildRegistry(
            new FakeForemanDirectory(Config("GC", CrewRole.GC)),
            sink,
            new FakeCliProcessRunner { NextResult = new CliRunResult(true, "GC says hello", "", 0) });

        var state = new DashboardState { HomeOfficeAddress = "http://localhost:1/", GcForemanName = "GC" };
        var pending = new PendingBossTurns();

        // Exactly what HandleBossLine does: dispatch, record the id, carry on.
        state.Transcript.Add(new TranscriptLine("Boss", "hello"));
        pending.Track(registry.StartJob("GC", "hello"), "GC");

        await DrainUntilAnnouncedAsync(sink, pending, state);

        Assert.Equal(2, state.Transcript.Count);
        Assert.Equal("GC", state.Transcript[1].Speaker);
        Assert.Equal("GC says hello", state.Transcript[1].Text);
        Assert.False(state.Transcript[1].IsError);
        Assert.Equal(0, pending.Count);
    }

    /// <summary>
    /// A driven Foreman's reply belongs in that Foreman's pane, not GC's. Getting
    /// this wrong is the divergent-conversation bug wearing a different hat.
    /// </summary>
    [Fact]
    public async Task DrivenTurn_LandsInThatForemansTranscriptNotGcs()
    {
        var sink = new JobStatusSink();
        var registry = BuildRegistry(
            new FakeForemanDirectory(Config("GC", CrewRole.GC), Config("Frontend", CrewRole.Foreman)),
            sink,
            new FakeCliProcessRunner { NextResult = new CliRunResult(true, "Frontend says hi", "", 0) });

        var state = new DashboardState { HomeOfficeAddress = "http://localhost:1/", GcForemanName = "GC" };
        var pending = new PendingBossTurns();

        Assert.Equal(
            BossCommandResult.Handled,
            DriveCommands.Apply(state, "/drive Frontend", new FakeForemanDirectory(
                Config("GC", CrewRole.GC), Config("Frontend", CrewRole.Foreman)).Find, registry.GetAllJobs()));

        var target = state.DrivenForeman!;
        state.TranscriptFor(target).Add(new TranscriptLine("Boss", "status?"));
        pending.Track(registry.StartJob(target, "status?"), target);

        await DrainUntilAnnouncedAsync(sink, pending, state);

        Assert.Contains(state.TranscriptFor("Frontend"), l => l.Text == "Frontend says hi");
        Assert.Empty(state.Transcript);
    }

    [Fact]
    public async Task FailedTurn_StillReportsBack()
    {
        var sink = new JobStatusSink();
        var registry = BuildRegistry(
            new FakeForemanDirectory(Config("GC", CrewRole.GC)),
            sink,
            new FakeCliProcessRunner { NextResult = new CliRunResult(false, "", "the CLI blew up", 1) });

        var state = new DashboardState { HomeOfficeAddress = "http://localhost:1/", GcForemanName = "GC" };
        var pending = new PendingBossTurns();

        pending.Track(registry.StartJob("GC", "hello"), "GC");

        await DrainUntilAnnouncedAsync(sink, pending, state);

        Assert.Single(state.Transcript);
        Assert.True(state.Transcript[0].IsError);
        Assert.Contains("blew up", state.Transcript[0].Text);
    }

    /// <summary>
    /// The Boss dispatches, then keeps typing. Every turn has to be reported, and
    /// jobs the Boss never started (a Worker, a GC-dispatched task) must not
    /// produce transcript lines at all.
    /// </summary>
    [Fact]
    public async Task ConcurrentTurns_AreAllReportedAndNothingElseIs()
    {
        const int bossTurns = 8;

        var sink = new JobStatusSink();
        var registry = BuildRegistry(
            new FakeForemanDirectory(Config("GC", CrewRole.GC), Config("Frontend", CrewRole.Foreman)),
            sink,
            new FakeCliProcessRunner { NextResult = new CliRunResult(true, "ok", "", 0) });

        var state = new DashboardState { HomeOfficeAddress = "http://localhost:1/", GcForemanName = "GC" };
        var pending = new PendingBossTurns();

        for (var i = 0; i < bossTurns; i++)
        {
            pending.Track(registry.StartJob("GC", $"turn {i}"), "GC");

            // Not a Boss turn: GC dispatching work of its own, on the same channel.
            registry.StartJob("Frontend", $"background {i}");
        }

        using var cts = new CancellationTokenSource(Timeout);
        while (state.Transcript.Count < bossTurns)
        {
            var record = await sink.Reader.ReadAsync(cts.Token);
            if (pending.TryTakeCompletion(record, out var speaker, out var line))
            {
                state.TranscriptFor(speaker).Add(line);
            }
        }

        Assert.Equal(bossTurns, state.Transcript.Count);
        Assert.Equal(0, pending.Count);
        Assert.Empty(state.TranscriptFor("Frontend"));
    }
}
