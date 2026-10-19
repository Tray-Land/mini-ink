using System.Diagnostics;
using System.Text.Json;

namespace MiniInk;

public sealed class NoteStore(string folder)
{
    private readonly Dictionary<Guid, Note> _notes = [];
    // Kept across unloads, so a save already under way for a deleted note can't bring it back.
    private readonly HashSet<Guid> _deleted = [];
    private readonly SemaphoreSlim _operations = new(1, 1);
    private bool _loaded;
    public string Folder { get; } = folder;
    public IReadOnlyList<Note> All => _notes.Values.OrderByDescending(n => n.Updated).ToArray();
    public Note? Find(Guid id) => _notes.GetValueOrDefault(id);
    public List<string> LoadErrors { get; } = [];
    public event EventHandler? Changed;

    public async Task LoadAsync()
    {
        await _operations.WaitAsync();
        try
        {
            if (_loaded)
                return;
            _notes.Clear();
            LoadErrors.Clear();
            Directory.CreateDirectory(Folder);
            foreach (string path in Directory.EnumerateFiles(Folder, "*.json"))
            {
                try
                {
                    Note note = JsonSerializer.Deserialize(await File.ReadAllTextAsync(path), NoteJsonContext.Default.Note)
                        ?? throw new InvalidDataException("Empty note document.");
                    if (note.FormatVersion != 1 || note.Id == Guid.Empty ||
                        Path.GetFileNameWithoutExtension(path) != note.Id.ToString("N") ||
                        note.Texts is null || note.Title is null || note.InkBase64 is null || note.Thumbnail is null ||
                        note.StrokeCount is < 0 ||
                        !double.IsFinite(note.CanvasWidth) || !double.IsFinite(note.CanvasHeight) ||
                        note.CanvasWidth < 100 || note.CanvasHeight < 100 || !Enum.IsDefined(note.Template) ||
                        note.Options is { IsValid: false } ||
                        note.Texts.Any(t => t is null || t.Text is null || t.Id == Guid.Empty ||
                            !double.IsFinite(t.X) || !double.IsFinite(t.Y) || t.X < 0 || t.Y < 0))
                        throw new InvalidDataException("Unsupported or invalid note document.");
                    if (note.InkBase64.Length > 0)
                        _ = Convert.FromBase64String(note.InkBase64);
                    _notes.Add(note.Id, note);
                }
                catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or JsonException or FormatException or ArgumentException)
                {
                    Debug.WriteLine($"Loading note '{path}' failed: {ex}");
                    LoadErrors.Add($"{Path.GetFileName(path)}: {ex.Message}");
                }
            }
            _loaded = true;
        }
        finally
        {
            _operations.Release();
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Unload()
    {
        if (_operations.CurrentCount == 0)
            return;
        _notes.Clear();
        _loaded = false;
    }

    public async Task SaveAsync(Note note)
    {
        await _operations.WaitAsync();
        try
        {
            if (_deleted.Contains(note.Id))
                return;
            await WriteAsync(note);
            _notes[note.Id] = note;
        }
        finally
        {
            _operations.Release();
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    // Notes saved before thumbnails were kept get one drawn later from their ink. It is
    // dropped if the note was saved again meanwhile, and the note keeps its date.
    public async Task AddThumbnailsAsync(IEnumerable<(Guid Id, string Ink, string Thumbnail)> thumbnails)
    {
        bool added = false;
        await _operations.WaitAsync();
        try
        {
            foreach ((Guid id, string ink, string thumbnail) in thumbnails)
            {
                if (thumbnail.Length == 0 || _notes.GetValueOrDefault(id) is not { Thumbnail: "" } note || note.InkBase64 != ink)
                    continue;
                note.Thumbnail = thumbnail;
                await WriteAsync(note);
                added = true;
            }
        }
        finally
        {
            _operations.Release();
        }
        if (added)
            Changed?.Invoke(this, EventArgs.Empty);
    }

    private async Task WriteAsync(Note note)
    {
        Directory.CreateDirectory(Folder);
        string path = Path.Combine(Folder, $"{note.Id:N}.json");
        string temporary = path + ".tmp";
        await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(note, NoteJsonContext.Default.Note));
        File.Move(temporary, path, overwrite: true);
    }

    public async Task DeleteAsync(Guid id)
    {
        await _operations.WaitAsync();
        try
        {
            File.Delete(Path.Combine(Folder, $"{id:N}.json"));
            _deleted.Add(id);
            _notes.Remove(id);
        }
        finally
        {
            _operations.Release();
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }
}
