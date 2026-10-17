using System.Diagnostics;
using System.Text.Json;

namespace MiniInk;

public sealed class NoteStore(string folder)
{
    private readonly Dictionary<Guid, Note> _notes = [];
    private readonly SemaphoreSlim _operations = new(1, 1);
    private bool _loaded;
    public string Folder { get; } = folder;
    public IReadOnlyList<Note> All => _notes.Values.OrderByDescending(n => n.Updated).ToArray();
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
                        note.Texts is null || note.Title is null || note.InkBase64 is null ||
                        note.RecognizedText is null || note.HandwritingKeywords is null || note.IndexError is null ||
                        note.StrokeCount is < 0 ||
                        !double.IsFinite(note.CanvasWidth) || !double.IsFinite(note.CanvasHeight) ||
                        note.CanvasWidth < 100 || note.CanvasHeight < 100 ||
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
            Directory.CreateDirectory(Folder);
            string path = Path.Combine(Folder, $"{note.Id:N}.json");
            string temporary = path + ".tmp";
            await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(note, NoteJsonContext.Default.Note));
            File.Move(temporary, path, overwrite: true);
            _notes[note.Id] = note;
        }
        finally
        {
            _operations.Release();
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }
}
