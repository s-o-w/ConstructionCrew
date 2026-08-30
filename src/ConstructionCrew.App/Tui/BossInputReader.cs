using System.Threading.Channels;

namespace ConstructionCrew.App.Tui;

/// <summary>
/// Moves <c>Console.ReadLine()</c> off the Boss loop and onto a dedicated thread
/// feeding a <see cref="Channel{T}"/>, so the loop can re-render on a job
/// transition while the Boss is mid-sentence and never blocks a whole GC turn on
/// a line of input.
///
/// <para>
/// The read is <b>permission-gated, one line at a time</b>. The thread waits for a
/// <see cref="Resume"/> before every single <c>ReadLine</c>, and the loop only
/// grants the next one after it has finished processing the previous line. That
/// handshake is what keeps the modal wizards (<c>/hire</c>, <c>/fire</c>,
/// <c>/settings</c>, every "press enter to continue") working: while one of them
/// owns the console, this thread is parked on the gate, not competing for stdin.
/// A free-running reader would race those prompts for the same keystrokes.
/// </para>
///
/// <para>
/// It costs nothing against the "type while GC works" gate: dispatching a Boss
/// turn is <c>JobRegistry.StartJob</c>, which returns a job id immediately, so
/// permission is handed straight back.
/// </para>
///
/// <para>
/// <paramref name="readLine"/> is injected rather than called directly so the
/// handshake itself is testable without a console.
/// </para>
/// </summary>
internal sealed class BossInputReader : IDisposable
{
    private readonly Func<string?> _readLine;

    private readonly Channel<string> _lines = Channel.CreateUnbounded<string>(
        new UnboundedChannelOptions { SingleWriter = true, SingleReader = true });

    /// <summary>
    /// Starts unset: nothing is read until the loop has rendered once and asked
    /// for a line.
    /// </summary>
    private readonly ManualResetEventSlim _permit = new(initialState: false);

    private readonly CancellationTokenSource _stopping = new();

    private Thread? _thread;

    public BossInputReader(Func<string?> readLine)
    {
        _readLine = readLine;
    }

    /// <summary>Completes when stdin reaches EOF (a piped or redirected run).</summary>
    public ChannelReader<string> Reader => _lines.Reader;

    public void Start()
    {
        // Background: a thread parked inside Console.ReadLine() cannot be
        // interrupted, so it must not be able to hold the process open.
        _thread = new Thread(Pump) { IsBackground = true, Name = "boss-input" };
        _thread.Start();
    }

    /// <summary>
    /// Grants permission to read exactly one more line. The caller owes exactly
    /// one call per line it consumed: the gate is a latch, not a counter, so two
    /// unbalanced Resumes in a row could let a second read start before the loop
    /// has finished with the first. The Boss loop's <c>wantsInput</c> flag is what
    /// keeps that one-to-one.
    /// </summary>
    public void Resume() => _permit.Set();

    private void Pump()
    {
        try
        {
            while (true)
            {
                _permit.Wait(_stopping.Token);

                // Re-checked explicitly: Dispose cancels AND sets the gate, and a
                // Wait that observes the set first returns normally. Without this
                // the thread would go on to read one more line after shutdown.
                if (_stopping.IsCancellationRequested)
                {
                    return;
                }

                // Reset before reading, never after: the loop's next Resume must
                // apply to the line after this one, and consuming the permit up
                // front is what guarantees only one read is ever outstanding.
                _permit.Reset();

                var line = _readLine();
                if (line is null)
                {
                    // EOF -- treated exactly like the old loop's `input is null`
                    // break, surfaced as channel completion.
                    return;
                }

                _lines.Writer.TryWrite(line);
            }
        }
        catch (Exception)
        {
            // Deliberately broad. This is a bare Thread: an escaping exception
            // takes the whole process down, and stdin failing is not worth losing
            // the Home Office and every in-flight job over. Completing the channel
            // instead unwinds the Boss loop the same way EOF does.
        }
        finally
        {
            _lines.Writer.TryComplete();
        }
    }

    /// <summary>
    /// Cancels the gate and pokes it, so a thread parked on the permit unwinds.
    /// A thread already inside <c>Console.ReadLine()</c> cannot be woken -- it is
    /// a background thread and dies with the process, which is why the primitives
    /// here are deliberately not disposed out from under it.
    /// </summary>
    public void Dispose()
    {
        _stopping.Cancel();
        _permit.Set();
    }
}
