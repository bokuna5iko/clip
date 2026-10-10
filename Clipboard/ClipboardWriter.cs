using System.Collections.Specialized;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Media.Imaging;
using Clip.Models;

namespace Clip.Clipboard;

public static class ClipboardWriter
{
    public static bool TryRestore(ClipItem item)
    {
        return item.Kind switch
        {
            "text" => TrySetText(item.TextContent),
            "image" => TrySetImage(item.ImagePath),
            "files" => TrySetFiles(item.FilesJson),
            _ => false
        };
    }

    private static bool TrySetText(string? text)
    {
        if (string.IsNullOrEmpty(text))
            return false;
        System.Windows.Clipboard.SetText(text);
        return true;
    }

    private static bool TrySetImage(string? imagePath)
    {
        if (string.IsNullOrEmpty(imagePath) || !File.Exists(imagePath))
            return false;

        var bi = new BitmapImage();
        bi.BeginInit();
        bi.CacheOption = BitmapCacheOption.OnLoad;
        bi.UriSource = new Uri(imagePath, UriKind.Absolute);
        bi.EndInit();
        bi.Freeze();
        System.Windows.Clipboard.SetImage(bi);
        return true;
    }

    private static bool TrySetFiles(string? filesJson)
    {
        if (string.IsNullOrEmpty(filesJson))
            return false;

        string[] paths;
        try
        {
            paths = JsonSerializer.Deserialize<string[]>(filesJson) ?? Array.Empty<string>();
        }
        catch
        {
            return false;
        }

        var existing = paths.Where(File.Exists).ToArray();
        if (existing.Length == 0)
            return false;

        var collection = new StringCollection();
        collection.AddRange(existing);
        System.Windows.Clipboard.SetFileDropList(collection);
        return true;
    }
}
