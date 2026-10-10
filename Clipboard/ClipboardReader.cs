using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Media.Imaging;
using Clip.Data;

namespace Clip.Clipboard;

public static class ClipboardReader
{
    private const int MaxAttempts = 5;
    private const int RetryDelayMs = 50;

    public sealed record CaptureResult(string Kind, string? TextContent, string? ImagePath, string? FilesJson, string Preview);

    public static CaptureResult? TryCapture(ClipStore store)
    {
        for (var attempt = 0; attempt < MaxAttempts; attempt++)
        {
            try
            {
                var result = TryCaptureOnce(store);
                if (result is not null)
                    return result;
                return null;
            }
            catch
            {
                Thread.Sleep(RetryDelayMs);
            }
        }

        return null;
    }

    private static CaptureResult? TryCaptureOnce(ClipStore store)
    {
        if (System.Windows.Clipboard.ContainsFileDropList())
        {
            var files = System.Windows.Clipboard.GetFileDropList();
            var paths = new string[files.Count];
            files.CopyTo(paths, 0);
            if (paths.Length == 0)
                return null;
            var json = JsonSerializer.Serialize(paths);
            var preview = ClipStore.BuildFilesPreview(paths);
            return new CaptureResult("files", null, null, json, preview);
        }

        if (System.Windows.Clipboard.ContainsImage())
        {
            var image = System.Windows.Clipboard.GetImage();
            if (image is null)
                return null;

            byte[] pngBytes;
            using (var ms = new MemoryStream())
            {
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(image));
                encoder.Save(ms);
                pngBytes = ms.ToArray();
            }

            if (store.IsImageTooLarge(pngBytes.Length))
                return null;

            var id = Guid.NewGuid().ToString("N");
            var path = store.NewImagePath(id);
            File.WriteAllBytes(path, pngBytes);
            return new CaptureResult("image", null, path, null, "Снимок");
        }

        if (System.Windows.Clipboard.ContainsText())
        {
            var text = System.Windows.Clipboard.GetText();
            if (string.IsNullOrWhiteSpace(text))
                return null;
            if (store.IsTextTooLarge(text))
                return null;
            var preview = ClipStore.BuildTextPreview(text);
            return new CaptureResult("text", text, null, null, preview);
        }

        return null;
    }
}
