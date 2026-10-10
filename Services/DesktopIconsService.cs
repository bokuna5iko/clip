using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace Clip.Services;

public static class DesktopIconsService
{
    private const string KeyPath = @"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced";
    private const string ValueName = "HideIcons";
    private const int ToggleDesktopIcons = 0x7402;
    private const int WmCommand = 0x0111;

    private static int? _previousHideIcons;
    private static bool _changed;

    public static void Hide()
    {
        using var key = Registry.CurrentUser.OpenSubKey(KeyPath, true);
        if (key is null)
            return;

        _previousHideIcons = key.GetValue(ValueName) is int value ? value : 0;
        if (_previousHideIcons == 1)
            return;

        key.SetValue(ValueName, 1);
        Toggle();
        _changed = true;
    }

    public static void Restore()
    {
        if (!_changed)
            return;

        using var key = Registry.CurrentUser.OpenSubKey(KeyPath, true);
        key?.SetValue(ValueName, _previousHideIcons ?? 0);
        Toggle();
        _changed = false;
    }

    private static void Toggle()
    {
        var progman = FindWindow("Progman", "Program Manager");
        if (progman == IntPtr.Zero)
            progman = FindWindow("Progman", null);
        if (progman == IntPtr.Zero)
            return;
        SendMessage(progman, WmCommand, (IntPtr)ToggleDesktopIcons, IntPtr.Zero);
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindow(string? className, string? windowName);

    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);
}
