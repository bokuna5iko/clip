namespace Clip.Models;

public sealed class ClipItem
{
    public string Id { get; init; } = "";
    public string Kind { get; init; } = "";
    public DateTime CreatedAt { get; set; }
    public string? TextContent { get; init; }
    public string? ImagePath { get; init; }
    public string? FilesJson { get; init; }
    public string Preview { get; init; } = "";

    public string KindLabel => Kind switch
    {
        "text" => "Т",
        "image" => "С",
        "files" => "Ф",
        _ => "?"
    };

    public string SecondaryLine
    {
        get
        {
            var time = CreatedAt.ToLocalTime().ToString("dd.MM HH:mm");
            if (Kind != "files" || string.IsNullOrEmpty(FilesJson))
                return time;

            try
            {
                var paths = System.Text.Json.JsonSerializer.Deserialize<string[]>(FilesJson);
                if (paths is { Length: > 0 })
                {
                    var folder = Path.GetDirectoryName(paths[0]);
                    if (!string.IsNullOrEmpty(folder))
                        return $"{time} · {folder}";
                }
            }
            catch
            {
                // ignore
            }

            return time;
        }
    }
}
