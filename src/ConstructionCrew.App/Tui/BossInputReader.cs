using System.Threading.Channels;

namespace ConstructionCrew.App.Tui;

/// <summary>
/// Moves <c>Console.ReadLine()</c> off the Boss loop and onto a dedicated
/// thread feeding a <see cref="Channel{T}"/>, so the loop can re-render on a
/// job transition while the Boss is mid-sentence.
///
/// <para>
/// The read is permission-gated, one line at a time: the thread waits for
/// <see cref="Resume"/> before every <c>ReadLine</c>, and the loop grants the
/// next one only after finishing the previous line. This is what keeps the
/// modal wizards (<c>/hire</c>, <c>/fire</c>, <c>/settings</c>) from racing
/// this thread for stdin while one of them owns the console.
/// </para>
///
/// <para>
/// <paramref name="readLine"/> is injected rather than called directly so the
/// handshake is testable without a console.
/// </para>
/// </summary>
internal sealed class BossInputReader : IDisposable
{
    private readonly Func<string?> _readLine;

    private readonly Channel<string> _lines = Channel.CreateUnbounded<string>(
        new UnboundedChannelOptions { SingleWriter = true, SingleReader = true });

    /// <summary>Starts unset: nothing is read until the loop asks for a line.</summary>
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
        // Background: a thread parked inside Console.ReadLine() can't be
        // interrupted, so it must not hold the process open.
        _thread = new Thread(Pump) { IsBackground = true, Name = "boss-input" };
        _thread.Start();
    }

    /// <summary>
    /// Grants permission to read one more line. The gate is a latch, not a
    /// counter, so two unbalanced calls in a row could let a second read start
    /// before the loop finishes the first. The Boss loop's <c>wantsInput</c>
    /// flag keeps calls one-to-one with lines consumed.
    /// </summary>
    public void Resume() => _permit.Set();

    private void Pump()
    {
        try
        {
            while (true)
            {
                _permit.Wait(_stopping.Token);

                // Re-checked explicitly: Dispose cancels AND sets the gate, and
                // a Wait that observes the set first returns normally. Without
                // this the thread would read one more line after shutdown.
                if (_stopping.IsCancellationRequested)
                {
                    return;
                }

                // Reset before reading, never after: consuming the permit up
                // front guarantees only one read is ever outstanding.
                _permit.Reset();

                var line = _readLine();
                if (line is null)
                {
                    // EOF: surfaced as channel completion.
                    return;
                }

                _lines.Writer.TryWrite(line);
            }
        }
        catch (Exception)
        {
            // Deliberately broad: this is a bare Thread, and an escaping
            // exception takes the whole process down. Completing the channel
            // instead unwinds the Boss loop the same way EOF does.
        }
        finally
        {
            _lines.Writer.TryComplete();
        }
    }

    /// <summary>
    /// Cancels the gate and pokes it, so a thread parked on the permit unwinds.
    /// A thread already inside <c>Console.ReadLine()</c> can't be woken; it's a
    /// background thread and dies with the process.
    /// </summary>
    public void Dispose()
    {
        _stopping.Cancel();
        _permit.Set();
    }
}
