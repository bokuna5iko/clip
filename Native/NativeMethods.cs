using System.Runtime.InteropServices;

namespace Clip.Native;

internal static class NativeMethods
{
    public const int WM_HOTKEY = 0x0312;
    public const int WM_CLIPBOARDUPDATE = 0x031D;

    public const uint MOD_CONTROL = 0x0002;
    public const uint MOD_SHIFT = 0x0004;
    public const int VkV = 0x56;
    public const int VkSpace = 0x20;

    public const int HotkeyId = 9001;
    public const int ShelfHotkeyId = 9002;

    public static readonly IntPtr HwndBottom = new(1);
    public const uint SwpNoMove = 0x0001;
    public const uint SwpNoSize = 0x0002;
    public const uint SwpNoActivate = 0x0010;

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool AddClipboardFormatListener(IntPtr hwnd);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool RemoveClipboardFormatListener(IntPtr hwnd);

    public const int DwmwaWindowCornerPreference = 33;
    public const int DwmwcpRound = 2;

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool SetWindowPos(
        IntPtr hWnd,
        IntPtr hWndInsertAfter,
        int x,
        int y,
        int cx,
        int cy,
        uint flags);

    [DllImport("dwmapi.dll")]
    public static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int pvAttribute, int cbAttribute);
}
