namespace MiniInk;

// The popup's pad of notes: a blank page on top, then saved notes newest first.
// The order is captured when the popup opens, so editing a note while flipping
// does not reshuffle the pile under the user.
public sealed class NoteStack
{
    private readonly List<Guid> _ids = [];
    public int Count => _ids.Count;

    public void Reset(IEnumerable<Note> notes, IReadOnlySet<Guid> away)
    {
        _ids.Clear();
        _ids.AddRange(notes.Where(n => !away.Contains(n.Id)).OrderByDescending(n => n.Updated).Select(n => n.Id));
    }

    // A page written on the blank sheet joins the top of the pile.
    public void Remember(Guid id)
    {
        if (!_ids.Contains(id))
            _ids.Insert(0, id);
    }

    public void Retain(Func<Guid, bool> keep) => _ids.RemoveAll(id => !keep(id));
    public int IndexOf(Guid? id) => id is Guid value ? _ids.IndexOf(value) : -1;
    public bool CanGoOlder(Guid? current) => IndexOf(current) + 1 < _ids.Count;
    public bool CanGoNewer(Guid? current) => IndexOf(current) >= 0;
    public Guid? Older(Guid? current) => CanGoOlder(current) ? _ids[IndexOf(current) + 1] : null;
    // Null is the blank page above the newest note.
    public Guid? Newer(Guid? current) => IndexOf(current) is > 0 and int index ? _ids[index - 1] : null;
    public string Describe(Guid? current) => IndexOf(current) is >= 0 and int index
        ? $"{index + 1} of {_ids.Count}" : "New page";
}

public static class FreshPagePolicy
{
    public const int DefaultMinutes = 5;
    // Minutes away before the popup opens on a blank page; 0 always reopens the last note.
    public static IReadOnlyList<int> Choices { get; } = [1, 5, 15, 60, 0];

    public static bool ShouldStartFresh(DateTimeOffset? lastUsed, DateTimeOffset now, int minutes) =>
        minutes > 0 && (lastUsed is not DateTimeOffset last || now - last >= TimeSpan.FromMinutes(minutes));

    public static string Label(int minutes) => minutes switch
    {
        <= 0 => "Never",
        1 => "After 1 minute",
        60 => "After 1 hour",
        _ when minutes % 60 == 0 => $"After {minutes / 60} hours",
        _ => $"After {minutes} minutes"
    };
}
