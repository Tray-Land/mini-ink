using System.Text.Json;
using System.Text.Json.Serialization;

namespace MiniInk;

// The kinds of pen a toolbar can hold. The eraser and the ruler are always offered and are not pens.
[JsonConverter(typeof(JsonStringEnumConverter<PenKind>))]
public enum PenKind
{
    Ballpoint,
    Pencil,
    Highlighter
}

public static class PenKinds
{
    public static PenKind[] All { get; } = Enum.GetValues<PenKind>();

    public static string Label(PenKind kind) => kind switch
    {
        PenKind.Ballpoint => "Pen",
        PenKind.Pencil => "Pencil",
        _ => "Highlighter"
    };

    // A pen or pencil writes in ink that shows on the page, black on a light one and white on a dark one,
    // until a color is picked. A highlighter keeps its own yellow.
    public static bool FollowsPage(PenKind kind) => kind != PenKind.Highlighter;
}

// One pen in the toolbar and how it was last left.
public sealed record PenSpec
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N");
    public PenKind Kind { get; init; }
    // The ink as ARGB; null is the pen's own (see PenKinds.FollowsPage).
    public uint? Color { get; init; }
    // The stroke width; null is the pen's own.
    public double? Size { get; init; }
}

// The pens on every toolbar, in order, and the one last picked up. Changes make a new set, which is
// what PenStore remembers between sessions.
public sealed class PenSet
{
    // The popup's toolbar is only so wide.
    public const int MaxPens = 5;
    public const double MaxSize = 100;

    public PenSpec[] Pens { get; init; } = [];
    // Null is the first pen.
    public string? ActiveId { get; init; }

    public static PenSet Default => new() { Pens = [.. PenKinds.All.Select(kind => new PenSpec { Kind = kind })] };

    [JsonIgnore]
    public bool CanAdd => Pens.Length < MaxPens;

    // A toolbar always has a pen to write with.
    [JsonIgnore]
    public bool CanRemove => Pens.Length > 1;

    [JsonIgnore]
    public bool IsValid => Pens.Length is >= 1 and <= MaxPens &&
        Pens.All(pen => pen is { Id.Length: > 0 } && Enum.IsDefined(pen.Kind) && (pen.Size is null || pen.Size is > 0 and <= MaxSize)) &&
        Pens.Select(pen => pen.Id).Distinct().Count() == Pens.Length;

    [JsonIgnore]
    public PenSpec Active => Find(ActiveId) ?? Pens[0];

    public PenSpec? Find(string? id) => Pens.FirstOrDefault(pen => pen.Id == id);

    // "Pen", then "Pen 2" for a second one, so pens of a kind can be told apart.
    public string Label(PenSpec pen)
    {
        int number = Pens.Where(other => other.Kind == pen.Kind).ToList().FindIndex(other => other.Id == pen.Id) + 1;
        return number > 1 ? $"{PenKinds.Label(pen.Kind)} {number}" : PenKinds.Label(pen.Kind);
    }

    public int IndexOf(string id) => Array.FindIndex(Pens, pen => pen.Id == id);

    // A new pen goes on the end.
    public PenSet Add(PenKind kind) => CanAdd
        ? new PenSet { Pens = [.. Pens, new PenSpec { Kind = kind }], ActiveId = ActiveId }
        : this;

    // The pen in hand goes back to the first when it is the one removed.
    public PenSet Remove(string id) => CanRemove && Find(id) is not null
        ? new PenSet { Pens = [.. Pens.Where(pen => pen.Id != id)], ActiveId = ActiveId == id ? null : ActiveId }
        : this;

    // Puts a pen at a place on the toolbar, counting from the first; the rest keep their order. A place past either
    // end is the end, so moving the first pen left, or the last right, changes nothing.
    public PenSet Move(string id, int index)
    {
        int from = IndexOf(id);
        index = Math.Clamp(index, 0, Pens.Length - 1);
        if (from < 0 || from == index)
            return this;
        List<PenSpec> moved = [.. Pens];
        moved.RemoveAt(from);
        moved.Insert(index, Pens[from]);
        return new PenSet { Pens = [.. moved], ActiveId = ActiveId };
    }

    // The same pen as it was left: its color and size.
    public PenSet Replace(PenSpec pen)
    {
        PenSpec? before = Find(pen.Id);
        return before is null || before == pen ? this
            : new PenSet { Pens = [.. Pens.Select(other => other.Id == pen.Id ? pen : other)], ActiveId = ActiveId };
    }

    public PenSet Activate(string id) => Find(id) is null || Active.Id == id ? this : new PenSet { Pens = Pens, ActiveId = id };

    // A toolbar is rebuilt when this is false, and only repainted when it is true.
    public bool SameTools(PenSet other) => Pens.Select(pen => (pen.Id, pen.Kind)).SequenceEqual(other.Pens.Select(pen => (pen.Id, pen.Kind)));

    public bool SameAs(PenSet other) => SameTools(other) && Pens.SequenceEqual(other.Pens) && Active.Id == other.Active.Id;

    // Saved pens that can't be read fall back to the defaults. A pen picked up that is no longer there is the first.
    public static PenSet Parse(string? json)
    {
        try
        {
            return json is not null && JsonSerializer.Deserialize(json, NoteJsonContext.Default.PenSet) is { IsValid: true } set
                ? set
                : Default;
        }
        catch (JsonException)
        {
            return Default;
        }
    }

    public string ToJson() => JsonSerializer.Serialize(this, NoteJsonContext.Default.PenSet);
}

public sealed class PenChange(PenSet pens, bool toolsChanged, object? source) : EventArgs
{
    public PenSet Pens { get; } = pens;
    // Pens were added, removed or put in another order, not just recolored.
    public bool ToolsChanged { get; } = toolsChanged;
    // Who made the change, so a toolbar can skip the ones it made itself.
    public object? Source { get; } = source;
}

// Remembers the pens between sessions: each change is written as it is made, and the next session starts from it.
public sealed class PenStore
{
    private readonly Action<string> _write;

    public PenStore(Func<string?> read, Action<string> write)
    {
        _write = write;
        Current = PenSet.Parse(read());
    }

    public PenSet Current { get; private set; }
    public event EventHandler<PenChange>? Changed;

    public void Edit(Func<PenSet, PenSet> change, object? source = null)
    {
        PenSet before = Current;
        PenSet after = change(before);
        if (ReferenceEquals(after, before) || after.SameAs(before) || !after.IsValid)
            return;
        Current = after;
        _write(after.ToJson());
        Changed?.Invoke(this, new PenChange(after, !after.SameTools(before), source));
    }

    public void Reset(object? source = null) => Edit(_ => PenSet.Default, source);
}
