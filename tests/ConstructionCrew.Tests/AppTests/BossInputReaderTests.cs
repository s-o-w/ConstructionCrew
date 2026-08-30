using System.Threading.Channels;
using ConstructionCrew.App.Tui;

namespace ConstructionCrew.Tests.AppTests;

/// <summary>
/// Phase 8a's input thread. The contract that matters is the handshake: exactly
/// one read may be outstanding at a time, and the loop -- not the reader --
/// decides when the next one starts. That is what keeps the modal wizards
/// (/hire, /fire, every "press enter to continue") from competing with this
/// thread for stdin, and it is the one place a deadlock could hide.
/// </summary>
public class BossInputReaderTests
{
    /// <summary>Bounded, so a hung handshake fails the test instead of hanging the run.</summary>
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    /// <summary>Short, and only ever used to assert that something does NOT happen.</summary>
    private static readonly TimeSpan NegativeWindow = TimeSpan.FromMilliseconds(250);

    private static async Task<string> ReadOneAsync(ChannelReader<string> reader)
    {
        using var cts = new CancellationTokenSource(Timeout);
        return await reader.ReadAsync(cts.Token);
    }

    [Fact]
    public async Task ReadsNothingUntilTheLoopAsksForALine()
    {
        var entered = new SemaphoreSlim(0);
        using var reader = new BossInputReader(() =>
        {
            entered.Release();
            return "hello";
        });

        reader.Start();

        Assert.False(await entered.WaitAsync(NegativeWindow));

        reader.Resume();

        Assert.True(await entered.WaitAsync(Timeout));
        Assert.Equal("hello", await ReadOneAsync(reader.Reader));
    }

    /// <summary>
    /// One Resume buys exactly one line. Without this, a wizard that owns the
    /// console would find the input thread already blocked in ReadLine, eating the
    /// keystrokes meant for its own prompt.
    /// </summary>
    [Fact]
    public async Task OneResumeGrantsExactlyOneRead()
    {
        var lines = new Queue<string>(["first", "second"]);
        var reads = 0;
        var entered = new SemaphoreSlim(0);

        using var reader = new BossInputReader(() =>
        {
            Interlocked.Increment(ref reads);
            entered.Release();
            // Null once drained -- stdin at EOF, not an exception.
            return lines.Count == 0 ? null : lines.Dequeue();
        });

        reader.Start();
        reader.Resume();

        Assert.Equal("first", await ReadOneAsync(reader.Reader));
        Assert.True(await entered.WaitAsync(Timeout));

        // The permit is spent: the second line stays unread until the loop has
        // finished processing the first one and grants another.
        Assert.False(await entered.WaitAsync(NegativeWindow));
        Assert.Equal(1, Volatile.Read(ref reads));

        reader.Resume();

        Assert.Equal("second", await ReadOneAsync(reader.Reader));
        Assert.Equal(2, Volatile.Read(ref reads));
    }

    /// <summary>EOF (a piped or redirected run) is the old loop's `input is null` break.</summary>
    [Fact]
    public async Task EndOfInput_CompletesTheChannel()
    {
        using var reader = new BossInputReader(() => null);
        reader.Start();
        reader.Resume();

        using var cts = new CancellationTokenSource(Timeout);
        await Assert.ThrowsAsync<ChannelClosedException>(async () => await reader.Reader.ReadAsync(cts.Token));
    }

    /// <summary>
    /// An escaping exception on a bare Thread takes the whole process with it --
    /// which would mean losing the Home Office and every in-flight job because
    /// stdin hiccuped. It has to unwind the loop the way EOF does instead.
    /// </summary>
    [Fact]
    public async Task ReadThatThrows_EndsTheLoopInsteadOfKillingTheProcess()
    {
        using var reader = new BossInputReader(() => throw new IOException("stdin went away"));
        reader.Start();
        reader.Resume();

        using var cts = new CancellationTokenSource(Timeout);
        await Assert.ThrowsAsync<ChannelClosedException>(async () => await reader.Reader.ReadAsync(cts.Token));
    }

    /// <summary>Disposing while the thread is parked on the gate unwinds it rather than wedging it.</summary>
    [Fact]
    public async Task Dispose_ReleasesAThreadParkedOnTheGate()
    {
        var reader = new BossInputReader(() => "never asked for");
        reader.Start();

        reader.Dispose();

        using var cts = new CancellationTokenSource(Timeout);
        await Assert.ThrowsAsync<ChannelClosedException>(async () => await reader.Reader.ReadAsync(cts.Token));
    }
}
