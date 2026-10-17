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

DateTimeOffset now = DateTimeOffset.UtcNow;
Note newest = new() { Updated = now };
Note middle = new() { Updated = now.AddHours(-1) };
Note oldest = new() { Updated = now.AddDays(-1) };
Note torn = new() { Updated = now.AddMinutes(-5) };
NoteStack stack = new();
stack.Reset([oldest, torn, newest, middle], new HashSet<Guid> { torn.Id });
Assert(stack.Count == 3 && stack.IndexOf(torn.Id) == -1, "Notes on the desktop must leave the pile.");
Assert(stack.Older(null) == newest.Id && !stack.CanGoNewer(null), "The blank page sits above the newest note.");
Assert(stack.Older(newest.Id) == middle.Id && stack.Older(middle.Id) == oldest.Id, "Older steps down the pile newest first.");
Assert(!stack.CanGoOlder(oldest.Id) && stack.Older(oldest.Id) is null, "The oldest note is the bottom of the pile.");
Assert(stack.Newer(newest.Id) is null && stack.CanGoNewer(newest.Id), "Newer from the newest note returns to the blank page.");
Assert(stack.Newer(oldest.Id) == middle.Id, "Newer steps back up the pile.");
Assert(stack.Describe(null) == "New page" && stack.Describe(middle.Id) == "2 of 3", "Positions count from the top of the pile.");
middle.Updated = now.AddMinutes(1);
Assert(stack.Older(newest.Id) == middle.Id, "Editing while flipping must not reshuffle the captured pile.");
Note written = new();
stack.Remember(written.Id);
stack.Remember(written.Id);
Assert(stack.Count == 4 && stack.IndexOf(written.Id) == 0, "A written blank page joins the top once.");
stack.Retain(id => id != newest.Id);
Assert(stack.Older(written.Id) == middle.Id, "Retain removes notes that left the pile.");

Assert(FreshPagePolicy.ShouldStartFresh(null, now, 5), "With no history the popup opens on a blank page.");
Assert(!FreshPagePolicy.ShouldStartFresh(now.AddMinutes(-2), now, 5), "A quick pick-up keeps the last note.");
Assert(FreshPagePolicy.ShouldStartFresh(now.AddMinutes(-5), now, 5), "After the chosen time away, a fresh page opens.");
Assert(!FreshPagePolicy.ShouldStartFresh(now.AddDays(-3), now, 0), "Never always keeps the last note.");
Assert(!FreshPagePolicy.ShouldStartFresh(now.AddMinutes(10), now, 5), "A clock moved backwards must not discard the open note.");
Assert(FreshPagePolicy.Choices.Contains(FreshPagePolicy.DefaultMinutes), "The default must be a selectable choice.");
Assert(FreshPagePolicy.Label(0) == "Never" && FreshPagePolicy.Label(1) == "After 1 minute" &&
    FreshPagePolicy.Label(15) == "After 15 minutes" && FreshPagePolicy.Label(60) == "After 1 hour", "Choice labels must read naturally.");

StickyPlacement offscreen = new() { NoteId = Guid.NewGuid(), X = 3000, Y = -200, Width = 500, Height = 2000, Pinned = true };
StickyPlacement fitted = offscreen.FitWithin(0, 0, 1920, 1040);
Assert(fitted.X == 1420 && fitted.Y == 0 && fitted.Width == 500 && fitted.Height == 1040 && fitted.Pinned,
    "Restored sticky windows must land fully inside the work area.");
StickyPlacement inside = new StickyPlacement { X = 100, Y = 200, Width = 400, Height = 480 }.FitWithin(0, 0, 1920, 1040);
Assert(inside.X == 100 && inside.Y == 200 && inside.Width == 400, "Visible sticky windows stay where they were.");

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

    StickyLayoutStore layout = new(Path.Combine(folder, "StickyNotes.json"));
    Assert(layout.Load().Count == 0, "A missing layout means no sticky windows.");
    Guid stuck = Guid.NewGuid();
    layout.Save([new StickyPlacement { NoteId = stuck, X = -10, Y = 20, Width = 400, Height = 480, Pinned = true },
        new StickyPlacement { NoteId = Guid.NewGuid(), Width = 10, Height = 10 }]);
    List<StickyPlacement> placements = layout.Load();
    Assert(placements.Count == 1 && placements[0].NoteId == stuck && placements[0].X == -10 && placements[0].Pinned,
        "Sticky placements must round-trip, dropping unusable sizes.");
    Assert(!File.Exists(layout.Path + ".tmp"), "Layout saves must replace the file atomically.");
    await File.WriteAllTextAsync(layout.Path, "[{broken");
    Assert(layout.Load().Count == 0, "A corrupt layout falls back to no windows instead of failing startup.");
}
finally
{
    foreach (string path in Directory.EnumerateFiles(folder))
        File.Delete(path);
    Directory.Delete(folder);
}
Console.WriteLine($"PASS: {assertions} search, stack, sticky layout, persistence and corruption assertions.");
