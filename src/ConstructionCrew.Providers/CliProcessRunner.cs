using CliWrap;
using CliWrap.Buffered;
using ConstructionCrew.Core.Abstractions;

namespace ConstructionCrew.Providers;

/// <summary>
/// The one place that actually spawns a process. Stdin is always closed
/// (PipeSource.Null): these CLIs run non-interactively via their own flags,
/// so if one somehow still tries to prompt, it hits EOF and fails fast
/// instead of hanging forever on input a redirected pipe will never deliver.
/// </summary>
public sealed class CliProcessRunner : ICliProcessRunner
{
    public async Task<CliRunResult> RunAsync(CliInvocation invocation, CancellationToken cancellationToken)
    {
        var command = Cli.Wrap(invocation.ExecutablePath)
            .WithArguments(invocation.Arguments)
            .WithWorkingDirectory(invocation.WorkingDirectory)
            .WithStandardInputPipe(PipeSource.Null)
            .WithValidation(CommandResultValidation.None);

        try
        {
            var result = await command.ExecuteBufferedAsync(cancellationToken);

            return new CliRunResult(
                Succeeded: result.ExitCode == 0,
                StandardOutput: result.StandardOutput,
                StandardError: result.StandardError,
                ExitCode: result.ExitCode);
        }
        catch (OperationCanceledException)
        {
            return new CliRunResult(
                Succeeded: false,
                StandardOutput: string.Empty,
                StandardError: "Cancelled (timeout or shutdown).",
                ExitCode: -1);
        }
        catch (Exception ex)
        {
            return new CliRunResult(
                Succeeded: false,
                StandardOutput: string.Empty,
                StandardError: ex.Message,
                ExitCode: -1);
        }
    }
}
