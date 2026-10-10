namespace Clip.Services;

public static class RecentFilesService
{
    public static IReadOnlyList<string> GetTargets(int limit = 40)
    {
        var folder = Environment.GetFolderPath(Environment.SpecialFolder.Recent);
        if (!Directory.Exists(folder))
            return Array.Empty<string>();

        var links = new DirectoryInfo(folder)
            .EnumerateFiles("*.lnk")
            .OrderByDescending(file => file.LastWriteTimeUtc)
            .Take(limit * 2)
            .ToList();

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<string>();
        foreach (var link in links)
        {
            string? target;
            try
            {
                target = Resolve(link.FullName);
            }
            catch
            {
                continue;
            }

            if (string.IsNullOrWhiteSpace(target) || !seen.Add(target))
                continue;
            if (!File.Exists(target) && !Directory.Exists(target))
                continue;
            result.Add(target);
            if (result.Count >= limit)
                break;
        }

        return result;
    }

    private static string? Resolve(string shortcutPath)
    {
        var type = Type.GetTypeFromProgID("WScript.Shell");
        if (type is null)
            return null;
        dynamic? shell = Activator.CreateInstance(type);
        if (shell is null)
            return null;
        var shortcut = shell.CreateShortcut(shortcutPath);
        string target = shortcut.TargetPath;
        return string.IsNullOrWhiteSpace(target) ? null : target;
    }
}
