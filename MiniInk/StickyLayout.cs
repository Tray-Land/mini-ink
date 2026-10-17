using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MiniInk;

// Where a torn-off note sits on the desktop, in physical pixels.
public sealed class StickyPlacement
{
    public Guid NoteId { get; set; }
    public int X { get; set; }
    public int Y { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
    public bool Pinned { get; set; }

    // Keeps a restored window reachable when its monitor is gone or smaller.
    public StickyPlacement FitWithin(int areaX, int areaY, int areaWidth, int areaHeight)
    {
        int width = Math.Min(Width, areaWidth);
        int height = Math.Min(Height, areaHeight);
        return new StickyPlacement
        {
            NoteId = NoteId,
            Pinned = Pinned,
            Width = width,
            Height = height,
            X = Math.Clamp(X, areaX, areaX + areaWidth - width),
            Y = Math.Clamp(Y, areaY, areaY + areaHeight - height)
        };
    }
}

public sealed class StickyLayoutStore(string path)
{
    public string Path { get; } = path;

    // Layout is a convenience, not content: anything unreadable falls back to no windows.
    public List<StickyPlacement> Load()
    {
        try
        {
            if (!File.Exists(Path))
                return [];
            List<StickyPlacement> placements = JsonSerializer.Deserialize(File.ReadAllText(Path),
                StickyLayoutJsonContext.Default.ListStickyPlacement) ?? [];
            return placements.Where(p => p is not null && p.NoteId != Guid.Empty && p.Width >= 100 && p.Height >= 100)
                .DistinctBy(p => p.NoteId).ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            Debug.WriteLine($"MiniInk: sticky layout unreadable: {ex}");
            return [];
        }
    }

    public void Save(IEnumerable<StickyPlacement> placements)
    {
        string temporary = Path + ".tmp";
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
        File.WriteAllText(temporary, JsonSerializer.Serialize(placements.ToList(), StickyLayoutJsonContext.Default.ListStickyPlacement));
        File.Move(temporary, Path, overwrite: true);
    }
}

[JsonSerializable(typeof(List<StickyPlacement>))]
internal partial class StickyLayoutJsonContext : JsonSerializerContext;
