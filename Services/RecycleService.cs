using System.Runtime.InteropServices;

namespace Clip.Services;

public static class RecycleService
{
    public static bool Send(string path)
    {
        if (!File.Exists(path) && !Directory.Exists(path))
            return false;

        var op = new ShFileOpStruct
        {
            wFunc = 3,
            pFrom = path + '\0' + '\0',
            fFlags = 0x40 | 0x10 | 0x400
        };
        return SHFileOperation(ref op) == 0 && !op.fAnyOperationsAborted;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ShFileOpStruct
    {
        public IntPtr hwnd;
        public uint wFunc;
        [MarshalAs(UnmanagedType.LPWStr)]
        public string pFrom;
        [MarshalAs(UnmanagedType.LPWStr)]
        public string? pTo;
        public ushort fFlags;
        [MarshalAs(UnmanagedType.Bool)]
        public bool fAnyOperationsAborted;
        public IntPtr hNameMappings;
        [MarshalAs(UnmanagedType.LPWStr)]
        public string? lpszProgressTitle;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHFileOperation(ref ShFileOpStruct lpFileOp);
}
