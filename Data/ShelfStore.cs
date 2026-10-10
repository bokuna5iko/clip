using Clip.Models;
using Microsoft.Data.Sqlite;

namespace Clip.Data;

public sealed class ShelfStore
{
    private readonly string _dbPath;

    public ShelfStore()
    {
        var root = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Clip");
        Directory.CreateDirectory(root);
        _dbPath = Path.Combine(root, "clip.db");
        EnsureSchema();
        RemoveMissing();
    }

    private SqliteConnection Open()
    {
        var conn = new SqliteConnection($"Data Source={_dbPath}");
        conn.Open();
        return conn;
    }

    private void EnsureSchema()
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS shelf_state (
                key TEXT PRIMARY KEY,
                value TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS shelf_groups (
                id TEXT PRIMARY KEY,
                name TEXT NOT NULL,
                x REAL NOT NULL,
                y REAL NOT NULL
            );
            CREATE TABLE IF NOT EXISTS shelf_books (
                id TEXT PRIMARY KEY,
                group_id TEXT NOT NULL,
                path TEXT NOT NULL,
                position INTEGER NOT NULL
            );
            CREATE TABLE IF NOT EXISTS shelf_pins (
                id TEXT PRIMARY KEY,
                path TEXT NOT NULL,
                position INTEGER NOT NULL
            );
            """;
        cmd.ExecuteNonQuery();
    }

    public string? GetState(string key)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT value FROM shelf_state WHERE key = @key";
        cmd.Parameters.AddWithValue("@key", key);
        return cmd.ExecuteScalar() as string;
    }

    public void SetState(string key, string value)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO shelf_state (key, value) VALUES (@key, @value)
            ON CONFLICT(key) DO UPDATE SET value = excluded.value
            """;
        cmd.Parameters.AddWithValue("@key", key);
        cmd.Parameters.AddWithValue("@value", value);
        cmd.ExecuteNonQuery();
    }

    public IReadOnlyList<ShelfGroup> GetGroups()
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT id, name, x, y FROM shelf_groups ORDER BY name";
        using var reader = cmd.ExecuteReader();
        var list = new List<ShelfGroup>();
        while (reader.Read())
        {
            list.Add(new ShelfGroup
            {
                Id = reader.GetString(0),
                Name = reader.GetString(1),
                X = reader.GetDouble(2),
                Y = reader.GetDouble(3)
            });
        }

        return list;
    }

    public IReadOnlyList<ShelfBook> GetBooks(string groupId)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT id, group_id, path, position FROM shelf_books
            WHERE group_id = @group
            ORDER BY position
            """;
        cmd.Parameters.AddWithValue("@group", groupId);
        return ReadBooks(cmd);
    }

    public IReadOnlyList<ShelfBook> GetAllBooks()
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT id, group_id, path, position FROM shelf_books ORDER BY position";
        return ReadBooks(cmd);
    }

    public IReadOnlyList<ShelfPin> GetPins()
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT id, path, position FROM shelf_pins ORDER BY position";
        using var reader = cmd.ExecuteReader();
        var list = new List<ShelfPin>();
        while (reader.Read())
        {
            list.Add(new ShelfPin
            {
                Id = reader.GetString(0),
                Path = reader.GetString(1),
                Position = reader.GetInt32(2)
            });
        }

        return list;
    }

    public ShelfGroup AddGroup(string name, double x, double y)
    {
        var group = new ShelfGroup
        {
            Id = Guid.NewGuid().ToString("N"),
            Name = string.IsNullOrWhiteSpace(name) ? "Группа" : name.Trim(),
            X = x,
            Y = y
        };
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "INSERT INTO shelf_groups (id, name, x, y) VALUES (@id, @name, @x, @y)";
        cmd.Parameters.AddWithValue("@id", group.Id);
        cmd.Parameters.AddWithValue("@name", group.Name);
        cmd.Parameters.AddWithValue("@x", group.X);
        cmd.Parameters.AddWithValue("@y", group.Y);
        cmd.ExecuteNonQuery();
        return group;
    }

    public void RenameGroup(string id, string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return;
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE shelf_groups SET name = @name WHERE id = @id";
        cmd.Parameters.AddWithValue("@name", name.Trim());
        cmd.Parameters.AddWithValue("@id", id);
        cmd.ExecuteNonQuery();
    }

    public void MoveGroup(string id, double x, double y)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE shelf_groups SET x = @x, y = @y WHERE id = @id";
        cmd.Parameters.AddWithValue("@x", x);
        cmd.Parameters.AddWithValue("@y", y);
        cmd.Parameters.AddWithValue("@id", id);
        cmd.ExecuteNonQuery();
    }

    public void DeleteGroup(string id)
    {
        using var conn = Open();
        using var tx = conn.BeginTransaction();
        using (var books = conn.CreateCommand())
        {
            books.Transaction = tx;
            books.CommandText = "DELETE FROM shelf_books WHERE group_id = @id";
            books.Parameters.AddWithValue("@id", id);
            books.ExecuteNonQuery();
        }

        using var groups = conn.CreateCommand();
        groups.Transaction = tx;
        groups.CommandText = "DELETE FROM shelf_groups WHERE id = @id";
        groups.Parameters.AddWithValue("@id", id);
        groups.ExecuteNonQuery();
        tx.Commit();
    }

    public bool AddBook(string groupId, string path)
    {
        if (!Exists(path) || ContainsBook(groupId, path))
            return false;

        var position = NextBookPosition(groupId);
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO shelf_books (id, group_id, path, position)
            VALUES (@id, @group, @path, @position)
            """;
        cmd.Parameters.AddWithValue("@id", Guid.NewGuid().ToString("N"));
        cmd.Parameters.AddWithValue("@group", groupId);
        cmd.Parameters.AddWithValue("@path", path);
        cmd.Parameters.AddWithValue("@position", position);
        cmd.ExecuteNonQuery();
        return true;
    }

    public void RemoveBook(string bookId) => DeleteBook(bookId, compact: true);

    public void MoveBook(string bookId, string targetGroupId, int targetIndex)
    {
        var book = FindBook(bookId);
        if (book is null || !Exists(book.Path))
            return;

        if (book.GroupId == targetGroupId)
        {
            Reorder(targetGroupId, bookId, targetIndex);
            return;
        }

        if (ContainsBook(targetGroupId, book.Path))
        {
            DeleteBook(bookId, compact: true);
            return;
        }

        DeleteBook(bookId, compact: true);
        InsertBookAt(targetGroupId, book.Path, targetIndex);
    }

    public bool AddPin(string path)
    {
        if (!Exists(path) || GetPins().Any(pin => PathsEqual(pin.Path, path)))
            return false;

        var position = GetPins().Count == 0 ? 0 : GetPins().Max(pin => pin.Position) + 1;
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "INSERT INTO shelf_pins (id, path, position) VALUES (@id, @path, @position)";
        cmd.Parameters.AddWithValue("@id", Guid.NewGuid().ToString("N"));
        cmd.Parameters.AddWithValue("@path", path);
        cmd.Parameters.AddWithValue("@position", position);
        cmd.ExecuteNonQuery();
        return true;
    }

    public void RemovePin(string pinId)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM shelf_pins WHERE id = @id";
        cmd.Parameters.AddWithValue("@id", pinId);
        cmd.ExecuteNonQuery();
    }

    public void RemovePath(string path)
    {
        using var conn = Open();
        using var tx = conn.BeginTransaction();
        using (var books = conn.CreateCommand())
        {
            books.Transaction = tx;
            books.CommandText = "DELETE FROM shelf_books WHERE path = @path COLLATE NOCASE";
            books.Parameters.AddWithValue("@path", path);
            books.ExecuteNonQuery();
        }

        using var pins = conn.CreateCommand();
        pins.Transaction = tx;
        pins.CommandText = "DELETE FROM shelf_pins WHERE path = @path COLLATE NOCASE";
        pins.Parameters.AddWithValue("@path", path);
        pins.ExecuteNonQuery();
        tx.Commit();
        CompactAll();
    }

    public void RemoveMissing()
    {
        var stale = GetAllBooks().Select(book => book.Path)
            .Concat(GetPins().Select(pin => pin.Path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(path => !Exists(path))
            .ToList();
        foreach (var path in stale)
            RemovePath(path);
    }

    private void Reorder(string groupId, string bookId, int targetIndex)
    {
        var books = GetBooks(groupId).ToList();
        var current = books.FindIndex(book => book.Id == bookId);
        if (current < 0)
            return;
        var item = books[current];
        books.RemoveAt(current);
        if (targetIndex < 0)
            targetIndex = 0;
        if (targetIndex > books.Count)
            targetIndex = books.Count;
        books.Insert(targetIndex, item);
        WriteBookOrder(groupId, books.Select(book => book.Id));
    }

    private void InsertBookAt(string groupId, string path, int targetIndex)
    {
        var books = GetBooks(groupId).Select(book => book.Id).ToList();
        var id = Guid.NewGuid().ToString("N");
        if (targetIndex < 0 || targetIndex > books.Count)
            targetIndex = books.Count;
        using (var conn = Open())
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO shelf_books (id, group_id, path, position)
                VALUES (@id, @group, @path, @position)
                """;
            cmd.Parameters.AddWithValue("@id", id);
            cmd.Parameters.AddWithValue("@group", groupId);
            cmd.Parameters.AddWithValue("@path", path);
            cmd.Parameters.AddWithValue("@position", targetIndex);
            cmd.ExecuteNonQuery();
        }

        books.Insert(targetIndex, id);
        WriteBookOrder(groupId, books);
    }

    private void WriteBookOrder(string groupId, IEnumerable<string> ids)
    {
        using var conn = Open();
        using var tx = conn.BeginTransaction();
        var position = 0;
        foreach (var id in ids)
        {
            using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = "UPDATE shelf_books SET position = @position WHERE id = @id AND group_id = @group";
            cmd.Parameters.AddWithValue("@position", position++);
            cmd.Parameters.AddWithValue("@id", id);
            cmd.Parameters.AddWithValue("@group", groupId);
            cmd.ExecuteNonQuery();
        }

        tx.Commit();
    }

    private void CompactAll()
    {
        foreach (var group in GetGroups())
            WriteBookOrder(group.Id, GetBooks(group.Id).Select(book => book.Id));
    }

    private ShelfBook? FindBook(string bookId)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT id, group_id, path, position FROM shelf_books WHERE id = @id";
        cmd.Parameters.AddWithValue("@id", bookId);
        return ReadBooks(cmd).FirstOrDefault();
    }

    private bool ContainsBook(string groupId, string path)
    {
        return GetBooks(groupId).Any(book => PathsEqual(book.Path, path));
    }

    private int NextBookPosition(string groupId)
    {
        var books = GetBooks(groupId);
        return books.Count == 0 ? 0 : books.Max(book => book.Position) + 1;
    }

    private void DeleteBook(string bookId, bool compact)
    {
        string? groupId = FindBook(bookId)?.GroupId;
        using (var conn = Open())
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "DELETE FROM shelf_books WHERE id = @id";
            cmd.Parameters.AddWithValue("@id", bookId);
            cmd.ExecuteNonQuery();
        }

        if (compact && groupId is not null)
            WriteBookOrder(groupId, GetBooks(groupId).Select(book => book.Id));
    }

    private static List<ShelfBook> ReadBooks(SqliteCommand cmd)
    {
        using var reader = cmd.ExecuteReader();
        var list = new List<ShelfBook>();
        while (reader.Read())
        {
            list.Add(new ShelfBook
            {
                Id = reader.GetString(0),
                GroupId = reader.GetString(1),
                Path = reader.GetString(2),
                Position = reader.GetInt32(3)
            });
        }

        return list;
    }

    public static bool Exists(string path) => File.Exists(path) || Directory.Exists(path);

    public static bool PathsEqual(string a, string b) =>
        string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
}
