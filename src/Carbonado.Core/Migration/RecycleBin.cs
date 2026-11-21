using System.Runtime.InteropServices;
using Carbonado.Core.Logging;

namespace Carbonado.Core.Migration;

/// <summary>Shell implementation of <see cref="IRecycleBin"/> (SHFileOperation with FOF_ALLOWUNDO, silent).</summary>
public sealed class RecycleBin : IRecycleBin
{
    private const uint FoDelete = 0x0003;
    private const ushort FofSilent = 0x0004;
    private const ushort FofNoConfirmation = 0x0010;
    private const ushort FofAllowUndo = 0x0040;
    private const ushort FofNoErrorUi = 0x0400;

    public static RecycleBin Instance { get; } = new();

    public bool Send(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        if (!File.Exists(path) && !Directory.Exists(path))
        {
            return true;
        }

        var op = new ShFileOpStruct
        {
            Func = FoDelete,
            From = Path.GetFullPath(path) + '\0', // the marshaller appends the second terminating null
            Flags = (ushort)(FofAllowUndo | FofNoConfirmation | FofSilent | FofNoErrorUi),
        };
        int rc = SHFileOperationW(ref op);
        bool ok = rc == 0 && op.AnyOperationsAborted == 0;
        if (!ok)
        {
            Log.Warn($"Recycle Bin move failed for {path} (code 0x{rc:X}).");
        }

        return ok;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ShFileOpStruct
    {
        public IntPtr Hwnd;
        public uint Func;
        [MarshalAs(UnmanagedType.LPWStr)]
        public string From;
        [MarshalAs(UnmanagedType.LPWStr)]
        public string? To;
        public ushort Flags;
        public int AnyOperationsAborted;
        public IntPtr NameMappings;
        [MarshalAs(UnmanagedType.LPWStr)]
        public string? ProgressTitle;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, EntryPoint = "SHFileOperationW")]
    private static extern int SHFileOperationW(ref ShFileOpStruct fileOp);
}
