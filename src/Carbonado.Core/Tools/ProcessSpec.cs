using System.Diagnostics;

namespace Carbonado.Core.Tools;

/// <summary>A child process to run: executable plus an argument list (never a shell command line).</summary>
/// <param name="FileName">Full path of the executable.</param>
/// <param name="Arguments">Arguments, each passed verbatim (quoted as needed by the runner).</param>
/// <param name="WorkingDirectory">Current directory of the child; null inherits the caller's.</param>
/// <param name="Timeout">Wall-clock limit; when exceeded the process tree is killed and the result is <see cref="ProcessResult.TimedOut"/>.</param>
/// <param name="CaptureStdout">Collect standard output as bytes (otherwise it is drained and discarded).</param>
/// <param name="Stdin">Bytes written to standard input, which is then closed (stdin is always redirected, so a tool never waits for console input).</param>
/// <param name="Priority">Scheduling priority of the child.</param>
public sealed record ProcessSpec(
    string FileName,
    IReadOnlyList<string> Arguments,
    string? WorkingDirectory = null,
    TimeSpan? Timeout = null,
    bool CaptureStdout = false,
    byte[]? Stdin = null,
    ProcessPriorityClass Priority = ProcessPriorityClass.BelowNormal);
