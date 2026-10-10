using System.Text;
using System.Text.Json;
using Clip.Models;
using Microsoft.Data.Sqlite;

namespace Clip.Data;

public sealed class ClipStore
{
    public const int MaxItems = 300;
    private const int MaxTextBytes = 1_048_576;
    private const int MaxImageBytes = 20_971_520;

    private readonly string _dbPath;
    private readonly string _imagesDir;

    public ClipStore()
    {
        var root = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Clip");
        Directory.CreateDirectory(root);
        _imagesDir = Path.Combine(root, "images");
        Directory.CreateDirectory(_imagesDir);
        _dbPath = Path.Combine(root, "clip.db");
        EnsureSchema();
        PurgeExpired();
    }

    public string ImagesDirectory => _imagesDir;

    private void EnsureSchema()
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS items (
                id TEXT PRIMARY KEY,
                kind TEXT NOT NULL,
                created_at TEXT NOT NULL,
                text_content TEXT NULL,
                image_path TEXT NULL,
                files_json TEXT NULL,
                preview TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS idx_items_created ON items(created_at DESC);
            CREATE TABLE IF NOT EXISTS meta (
                key TEXT PRIMARY KEY,
                value TEXT NOT NULL
            );
            """;
        cmd.ExecuteNonQuery();
    }

    private SqliteConnection Open()
    {
        var conn = new SqliteConnection($"Data Source={_dbPath}");
        conn.Open();
        return conn;
    }

    public IReadOnlyList<ClipItem> GetAll()
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT id, kind, created_at, text_content, image_path, files_json, preview
            FROM items
            ORDER BY created_at DESC
            """;
        using var reader = cmd.ExecuteReader();
        var list = new List<ClipItem>();
        while (reader.Read())
            list.Add(ReadRow(reader));
        return list;
    }

    public IReadOnlyList<ClipItem> Search(string query)
    {
        var q = query.Trim();
        if (string.IsNullOrEmpty(q))
            return GetAll();

        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT id, kind, created_at, text_content, image_path, files_json, preview
            FROM items
            WHERE lower(preview) LIKE @q
               OR lower(coalesce(text_content, '')) LIKE @q
               OR lower(coalesce(files_json, '')) LIKE @q
            ORDER BY created_at DESC
            """;
        cmd.Parameters.AddWithValue("@q", $"%{q.ToLowerInvariant()}%");
        using var reader = cmd.ExecuteReader();
        var list = new List<ClipItem>();
        while (reader.Read())
            list.Add(ReadRow(reader));
        return list;
    }

    /// <returns>True if a new row was inserted; false if an existing item was bumped.</returns>
    public bool InsertOrBump(string kind, string? textContent, string? imagePath, string? filesJson, string preview)
    {
        var newest = GetNewest();
        if (newest is not null && ContentEquals(newest, kind, textContent, imagePath, filesJson))
        {
            Bump(newest.Id);
            return false;
        }

        var id = Guid.NewGuid().ToString("N");
        var now = DateTime.UtcNow.ToString("o");
        using (var conn = Open())
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO items (id, kind, created_at, text_content, image_path, files_json, preview)
                VALUES (@id, @kind, @created, @text, @image, @files, @preview)
                """;
            cmd.Parameters.AddWithValue("@id", id);
            cmd.Parameters.AddWithValue("@kind", kind);
            cmd.Parameters.AddWithValue("@created", now);
            cmd.Parameters.AddWithValue("@text", (object?)textContent ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@image", (object?)imagePath ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@files", (object?)filesJson ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@preview", preview);
            cmd.ExecuteNonQuery();
            TrimExcess(conn);
        }

        PurgeExpired();
        return true;
    }

    public int GetRetentionDays()
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT value FROM meta WHERE key = 'retention_days'";
        var value = cmd.ExecuteScalar() as string;
        return int.TryParse(value, out var days) && days > 0 ? days : 0;
    }

    public void SetRetentionDays(int days)
    {
        if (days < 0)
            days = 0;
        if (days > 3650)
            days = 3650;

        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO meta (key, value) VALUES ('retention_days', @value)
            ON CONFLICT(key) DO UPDATE SET value = excluded.value
            """;
        cmd.Parameters.AddWithValue("@value", days.ToString());
        cmd.ExecuteNonQuery();
    }

    public void PurgeExpired()
    {
        var days = GetRetentionDays();
        if (days <= 0)
            return;

        var cutoff = DateTime.UtcNow.AddDays(-days).ToString("o");
        var doomed = new List<(string Id, string? ImagePath)>();
        using (var conn = Open())
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                SELECT id, image_path FROM items
                WHERE created_at < @cutoff
                """;
            cmd.Parameters.AddWithValue("@cutoff", cutoff);
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                var imagePath = reader.IsDBNull(1) ? null : reader.GetString(1);
                doomed.Add((reader.GetString(0), imagePath));
            }
        }

        if (doomed.Count == 0)
            return;

        foreach (var (_, imagePath) in doomed)
            DeleteImageFileIfAny(imagePath);

        using var deleteConn = Open();
        using var tx = deleteConn.BeginTransaction();
        foreach (var (id, _) in doomed)
        {
            using var del = deleteConn.CreateCommand();
            del.Transaction = tx;
            del.CommandText = "DELETE FROM items WHERE id = @id";
            del.Parameters.AddWithValue("@id", id);
            del.ExecuteNonQuery();
        }

        tx.Commit();
    }

    public void Delete(string id)
    {
        var item = GetById(id);
        if (item is null)
            return;

        DeleteImageFileIfAny(item.ImagePath);
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM items WHERE id = @id";
        cmd.Parameters.AddWithValue("@id", id);
        cmd.ExecuteNonQuery();
    }

    public void ClearAll()
    {
        var imagePaths = new List<string>();
        using (var conn = Open())
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT image_path FROM items WHERE image_path IS NOT NULL";
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                if (!reader.IsDBNull(0))
                    imagePaths.Add(reader.GetString(0));
            }
        }

        foreach (var path in imagePaths)
            DeleteImageFileIfAny(path);

        using var deleteConn = Open();
        using var del = deleteConn.CreateCommand();
        del.CommandText = "DELETE FROM items";
        del.ExecuteNonQuery();
    }

    private void Bump(string id)
    {
        var now = DateTime.UtcNow.ToString("o");
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE items SET created_at = @created WHERE id = @id";
        cmd.Parameters.AddWithValue("@created", now);
        cmd.Parameters.AddWithValue("@id", id);
        cmd.ExecuteNonQuery();
    }

    private ClipItem? GetNewest()
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT id, kind, created_at, text_content, image_path, files_json, preview
            FROM items ORDER BY created_at DESC LIMIT 1
            """;
        using var reader = cmd.ExecuteReader();
        return reader.Read() ? ReadRow(reader) : null;
    }

    private ClipItem? GetById(string id)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT id, kind, created_at, text_content, image_path, files_json, preview
            FROM items WHERE id = @id
            """;
        cmd.Parameters.AddWithValue("@id", id);
        using var reader = cmd.ExecuteReader();
        return reader.Read() ? ReadRow(reader) : null;
    }

    private void TrimExcess(SqliteConnection conn)
    {
        using var countCmd = conn.CreateCommand();
        countCmd.CommandText = "SELECT COUNT(*) FROM items";
        var count = Convert.ToInt32(countCmd.ExecuteScalar());
        if (count <= MaxItems)
            return;

        var toRemove = count - MaxItems;
        var doomed = new List<(string Id, string? ImagePath)>();
        using (var sel = conn.CreateCommand())
        {
            sel.CommandText = """
                SELECT id, image_path FROM items
                ORDER BY created_at ASC
                LIMIT @n
                """;
            sel.Parameters.AddWithValue("@n", toRemove);
            using var reader = sel.ExecuteReader();
            while (reader.Read())
            {
                var imagePath = reader.IsDBNull(1) ? null : reader.GetString(1);
                doomed.Add((reader.GetString(0), imagePath));
            }
        }

        foreach (var (id, imagePath) in doomed)
        {
            DeleteImageFileIfAny(imagePath);
            using var del = conn.CreateCommand();
            del.CommandText = "DELETE FROM items WHERE id = @id";
            del.Parameters.AddWithValue("@id", id);
            del.ExecuteNonQuery();
        }
    }

    private static void DeleteImageFileIfAny(string? imagePath)
    {
        if (string.IsNullOrEmpty(imagePath) || !File.Exists(imagePath))
            return;
        try
        {
            File.Delete(imagePath);
        }
        catch
        {
            // ignore
        }
    }

    private static bool ContentEquals(ClipItem item, string kind, string? text, string? imagePath, string? filesJson)
    {
        if (!string.Equals(item.Kind, kind, StringComparison.Ordinal))
            return false;

        return kind switch
        {
            "text" => string.Equals(item.TextContent, text, StringComparison.Ordinal),
            "image" => string.Equals(item.ImagePath, imagePath, StringComparison.Ordinal)
                       || (item.ImagePath is not null && imagePath is not null
                           && File.Exists(item.ImagePath) && File.Exists(imagePath)
                           && FilesEqual(item.ImagePath, imagePath)),
            "files" => FilesJsonEqual(item.FilesJson, filesJson),
            _ => false
        };
    }

    private static bool FilesJsonEqual(string? a, string? b)
    {
        var pa = NormalizePaths(a);
        var pb = NormalizePaths(b);
        if (pa.Length != pb.Length)
            return false;
        for (var i = 0; i < pa.Length; i++)
        {
            if (!string.Equals(pa[i], pb[i], StringComparison.OrdinalIgnoreCase))
                return false;
        }
        return true;
    }

    private static string[] NormalizePaths(string? json)
    {
        if (string.IsNullOrEmpty(json))
            return Array.Empty<string>();
        try
        {
            var paths = JsonSerializer.Deserialize<string[]>(json) ?? Array.Empty<string>();
            return paths.OrderBy(p => p, StringComparer.OrdinalIgnoreCase).ToArray();
        }
        catch
        {
            return Array.Empty<string>();
        }
    }

    private static bool FilesEqual(string pathA, string pathB)
    {
        try
        {
            var a = new FileInfo(pathA);
            var b = new FileInfo(pathB);
            if (a.Length != b.Length)
                return false;
            if (a.Length <= 512_000)
                return File.ReadAllBytes(pathA).AsSpan().SequenceEqual(File.ReadAllBytes(pathB));
            return string.Equals(ComputeSha256Hex(pathA), ComputeSha256Hex(pathB), StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private static string ComputeSha256Hex(string path)
    {
        using var stream = File.OpenRead(path);
        var hash = System.Security.Cryptography.SHA256.HashData(stream);
        return Convert.ToHexString(hash);
    }

    private static ClipItem ReadRow(SqliteDataReader reader)
    {
        return new ClipItem
        {
            Id = reader.GetString(0),
            Kind = reader.GetString(1),
            CreatedAt = DateTime.Parse(reader.GetString(2), null, System.Globalization.DateTimeStyles.RoundtripKind),
            TextContent = reader.IsDBNull(3) ? null : reader.GetString(3),
            ImagePath = reader.IsDBNull(4) ? null : reader.GetString(4),
            FilesJson = reader.IsDBNull(5) ? null : reader.GetString(5),
            Preview = reader.GetString(6)
        };
    }

    public static string BuildTextPreview(string text)
    {
        var line = text.Split('\n', '\r')[0];
        line = CollapseWhitespace(line);
        if (line.Length > 80)
            line = line[..80] + "…";
        return line;
    }

    public static string BuildFilesPreview(string[] paths)
    {
        if (paths.Length == 0)
            return "Файлы";
        if (paths.Length == 1)
            return Path.GetFileName(paths[0]);
        return $"{paths.Length} файлов · {Path.GetFileName(paths[0])}";
    }

    private static string CollapseWhitespace(string s)
    {
        var sb = new StringBuilder(s.Length);
        var prevSpace = false;
        foreach (var ch in s.Trim())
        {
            if (char.IsWhiteSpace(ch))
            {
                if (!prevSpace)
                {
                    sb.Append(' ');
                    prevSpace = true;
                }
            }
            else
            {
                sb.Append(ch);
                prevSpace = false;
            }
        }
        return sb.ToString();
    }

    public bool IsTextTooLarge(string text) => Encoding.UTF8.GetByteCount(text) > MaxTextBytes;
    public bool IsImageTooLarge(long byteCount) => byteCount > MaxImageBytes;

    public string NewImagePath(string id) => Path.Combine(_imagesDir, $"{id}.png");
}
