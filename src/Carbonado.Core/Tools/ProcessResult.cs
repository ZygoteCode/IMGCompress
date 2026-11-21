namespace Carbonado.Core.Tools;

/// <summary>Outcome of a finished child process.</summary>
/// <param name="ExitCode">Process exit code (meaningless when <paramref name="TimedOut"/>).</param>
/// <param name="Stdout">Captured standard output (empty unless <see cref="ProcessSpec.CaptureStdout"/>).</param>
/// <param name="Stderr">Standard error as text, capped at <see cref="ProcessRunner.MaxStderrChars"/> (head and tail kept).</param>
/// <param name="Elapsed">Wall-clock duration.</param>
/// <param name="TimedOut">The timeout expired and the process tree was killed.</param>
public sealed record ProcessResult(int ExitCode, byte[] Stdout, string Stderr, TimeSpan Elapsed, bool TimedOut)
{
    /// <summary>Exit code 0 and no timeout.</summary>
    public bool Success => ExitCode == 0 && !TimedOut;
}
