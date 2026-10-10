using Microsoft.Win32;

namespace Clip.Services;

public static class StartupService
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "Clip";

    public static bool IsEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, false);
        var value = key?.GetValue(ValueName) as string;
        if (string.IsNullOrEmpty(value))
            return false;
        var exe = Environment.ProcessPath ?? "";
        return string.Equals(value.Trim('"'), exe, StringComparison.OrdinalIgnoreCase);
    }

    public static void SetEnabled(bool enabled)
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, true)
                        ?? Registry.CurrentUser.CreateSubKey(RunKeyPath, true);
        if (!enabled)
        {
            key.DeleteValue(ValueName, false);
            return;
        }

        var exe = Environment.ProcessPath;
        if (string.IsNullOrEmpty(exe))
            return;
        key.SetValue(ValueName, $"\"{exe}\"");
    }
}
