using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Carbonado.Core.Tools;

/// <summary>
/// A Windows Job Object that sandboxes child processes: closing it (including when this process dies) kills every
/// process in it and their descendants (<c>JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE</c>); a crashing tool terminates at
/// once instead of showing an error dialog (<c>DIE_ON_UNHANDLED_EXCEPTION</c>); clipboard, desktop, display and
/// system-parameter access are denied.
/// </summary>
internal sealed partial class JobObject : IDisposable
{
    private const int JobObjectBasicUIRestrictions = 4;
    private const int JobObjectExtendedLimitInformation = 9;
    private const uint LimitDieOnUnhandledException = 0x0400;
    private const uint LimitKillOnJobClose = 0x2000;

    // READCLIPBOARD | WRITECLIPBOARD | SYSTEMPARAMETERS | DISPLAYSETTINGS | DESKTOP | EXITWINDOWS.
    private const uint UiRestrictions = 0x02 | 0x04 | 0x08 | 0x10 | 0x40 | 0x80;

    private readonly SafeJobHandle _handle;

    public JobObject()
    {
        _handle = CreateJobObjectW(IntPtr.Zero, IntPtr.Zero);
        if (_handle.IsInvalid)
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "CreateJobObject failed.");
        }

        var limits = new ExtendedLimitInformation
        {
            BasicLimitInformation = new BasicLimitInformation { LimitFlags = LimitKillOnJobClose | LimitDieOnUnhandledException },
        };
        if (!SetExtendedLimits(_handle, JobObjectExtendedLimitInformation, ref limits, (uint)Marshal.SizeOf<ExtendedLimitInformation>()))
        {
            int error = Marshal.GetLastPInvokeError();
            _handle.Dispose();
            throw new Win32Exception(error, "SetInformationJobObject (kill on close) failed.");
        }

        // Best effort: UI restrictions are hardening, not a correctness requirement.
        var ui = new BasicUiRestrictions { UiRestrictionsClass = UiRestrictions };
        _ = SetUiRestrictions(_handle, JobObjectBasicUIRestrictions, ref ui, (uint)Marshal.SizeOf<BasicUiRestrictions>());
    }

    /// <summary>Places <paramref name="process"/> (and the processes it later creates) in the job.</summary>
    /// <returns>False when the assignment failed (for example because the process already exited).</returns>
    public bool TryAssign(Process process) => AssignProcessToJobObject(_handle, process.SafeHandle);

    /// <summary>Terminates every process currently in the job; the job stays usable.</summary>
    public void Terminate(uint exitCode) => _ = TerminateJobObject(_handle, exitCode);

    public void Dispose() => _handle.Dispose();

    [StructLayout(LayoutKind.Sequential)]
    private struct BasicLimitInformation
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public nuint MinimumWorkingSetSize;
        public nuint MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public nuint Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IoCounters
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ExtendedLimitInformation
    {
        public BasicLimitInformation BasicLimitInformation;
        public IoCounters IoInfo;
        public nuint ProcessMemoryLimit;
        public nuint JobMemoryLimit;
        public nuint PeakProcessMemoryUsed;
        public nuint PeakJobMemoryUsed;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BasicUiRestrictions
    {
        public uint UiRestrictionsClass;
    }

    private sealed class SafeJobHandle() : SafeHandleZeroOrMinusOneIsInvalid(ownsHandle: true)
    {
        protected override bool ReleaseHandle() => CloseHandle(handle);
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial SafeJobHandle CreateJobObjectW(IntPtr jobAttributes, IntPtr name);

    [LibraryImport("kernel32.dll", EntryPoint = "SetInformationJobObject", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetExtendedLimits(SafeJobHandle job, int infoClass, ref ExtendedLimitInformation info, uint length);

    [LibraryImport("kernel32.dll", EntryPoint = "SetInformationJobObject", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetUiRestrictions(SafeJobHandle job, int infoClass, ref BasicUiRestrictions info, uint length);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool AssignProcessToJobObject(SafeJobHandle job, SafeProcessHandle process);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool TerminateJobObject(SafeJobHandle job, uint exitCode);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(IntPtr handle);
}
