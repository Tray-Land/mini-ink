using System.Text.Json;
using MiniInk;

int assertions = 0;
void Assert(bool condition, string message)
{
    if (!condition)
        throw new InvalidOperationException(message);
    assertions++;
}

Note note = new()
{
    Title = "Meeting ideas",
    Texts = [new TextNote { Text = "Blue notebook", X = 23.5, Y = 75 }],
    RecognizedText = "Call Alice",
    HandwritingKeywords = "Call Alicia",
    InkBase64 = Convert.ToBase64String([1, 2, 3, 4])
};
Assert(note.Matches("MEETING alice"), "Title and handwriting must match case-insensitively.");
Assert(note.Matches("blue\tAlicia"), "Typed text and recognition alternatives must be searched.");
Assert(!note.Matches("notebook missing"), "Every query term must match.");
Assert(note.Matches(" \t\n"), "Blank searches must return all notes.");
Assert(!new Note().Matches("empty page"), "Empty-state captions must not be indexed.");
Assert(new Note { InkBase64 = "AA==", StrokeCount = 0 }.Preview.StartsWith("An empty page"), "Empty ISF data must not look like an ink note.");

string folder = Path.Combine(Path.GetTempPath(), $"MiniInk-tests-{Guid.NewGuid():N}");
Directory.CreateDirectory(folder);
try
{
    NoteStore store = new(folder);
    await store.LoadAsync();
    await store.SaveAsync(note);
    Note updated = new()
    {
        Id = note.Id, Title = "Updated meeting", Texts = note.Texts,
        InkBase64 = note.InkBase64, RecognizedText = note.RecognizedText,
        HandwritingKeywords = note.HandwritingKeywords
    };
    await store.SaveAsync(updated);
    string path = Path.Combine(folder, $"{note.Id:N}.json");
    string json = await File.ReadAllTextAsync(path);
    Assert(!json.Contains("\"Preview\""), "Derived preview must not be serialized.");
    Assert(!File.Exists(path + ".tmp"), "Atomic save must finish with no pending temporary file.");
    store.Unload();
    await store.LoadAsync();
    Note restored = store.All.Single();
    Assert(restored.Title == updated.Title, "Replacement must survive reload.");
    Assert(restored.Texts.Single().X == 23.5 && restored.Texts.Single().Y == 75, "Object positions must survive reload.");
    Assert(restored.InkBase64 == note.InkBase64, "Original ink must survive reload.");
    Assert(restored.Matches("Alice") && restored.Matches("Alicia"), "Handwriting index must survive reload.");
    string badPath = Path.Combine(folder, $"{Guid.NewGuid():N}.json");
    await File.WriteAllTextAsync(badPath, "{invalid");
    store.Unload();
    await store.LoadAsync();
    Assert(store.All.Count == 1 && store.LoadErrors.Count == 1, "Corrupt notes must be reported without hiding valid notes.");
    Assert(await File.ReadAllTextAsync(badPath) == "{invalid", "Corrupt note must never be overwritten.");
    await File.WriteAllTextAsync(path, JsonSerializer.Serialize(updated, NoteJsonContext.Default.Note).Replace("\"CanvasWidth\":400", "\"CanvasWidth\":0"));
    store.Unload();
    await store.LoadAsync();
    Assert(store.All.Count == 0 && store.LoadErrors.Count == 2, "Invalid canvas dimensions must be rejected explicitly.");
}
finally
{
    foreach (string path in Directory.EnumerateFiles(folder))
        File.Delete(path);
    Directory.Delete(folder);
}
Console.WriteLine($"PASS: {assertions} search, persistence and corruption assertions.");
