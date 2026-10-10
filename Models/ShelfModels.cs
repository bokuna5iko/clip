namespace Clip.Models;

public sealed class ShelfGroup
{
    public string Id { get; init; } = "";
    public string Name { get; set; } = "";
    public double X { get; set; }
    public double Y { get; set; }
}

public sealed class ShelfBook
{
    public string Id { get; init; } = "";
    public string GroupId { get; init; } = "";
    public string Path { get; init; } = "";
    public int Position { get; init; }
}

public sealed class ShelfPin
{
    public string Id { get; init; } = "";
    public string Path { get; init; } = "";
    public int Position { get; init; }
}
