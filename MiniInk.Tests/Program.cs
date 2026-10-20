using System.Globalization;
using System.Numerics;
using System.Text.Json;
using MiniInk;

int assertions = 0;
void Assert(bool condition, string message)
{
    if (!condition)
        throw new InvalidOperationException(message);
    assertions++;
}

// Dates are searched as they read in the user's region; these checks read them in US English.
CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("en-US");
Note note = new()
{
    Title = "Meeting ideas",
    Texts = [new TextNote { Text = "Blue notebook", X = 23.5, Y = 75 }],
    InkBase64 = Convert.ToBase64String([1, 2, 3, 4]),
    Updated = new DateTimeOffset(2026, 10, 19, 15, 4, 0, TimeSpan.Zero)
};
Assert(note.Matches("MEETING notebook"), "Title and typed text must match case-insensitively.");
Assert(note.Matches("blue\toctober 2026") && note.Matches(note.DateLabel),
    "A note is found by when it was last changed, as the library shows it or written out.");
Assert(!note.Matches("notebook missing"), "Every query term must match.");
Assert(note.Matches(" \t\n"), "Blank searches must return all notes.");
Assert(!new Note().Matches("empty page"), "Empty-state captions must not be indexed.");
Assert(new Note { InkBase64 = "AA==", StrokeCount = 0 }.Preview.StartsWith("An empty page"), "Empty ISF data must not look like an ink note.");

Assert(InkThumbnail.Create([]) == "" && InkThumbnail.Create([[]]) == "", "No ink draws no thumbnail.");
Vector2[] line = Enumerable.Range(0, 201).Select(x => new Vector2(100 + x, 100)).ToArray();
string sketch = InkThumbnail.Create([line, [new Vector2(200, 140)]]);
Assert(sketch.StartsWith("M1,17.8L") && sketch.EndsWith("63,17.8M32,30.2L32,30.2"),
    "Large ink is scaled to fit the thumbnail, centered, with a dot drawn as a line of no length.");
Assert(sketch.Split(' ').Length < 80, "Points too close together to show are dropped.");
Assert(InkThumbnail.Create([[new Vector2(500, 500), new Vector2(510, 510)]]) == "M27,19L37,29", "Small ink keeps its size, centered.");

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

Assert(InputTools.Parse(null, InputTools.DefaultMouse) == InputTool.Text && InputTools.Parse(null, InputTools.DefaultTouch) == InputTool.Select,
    "With no saved choice, clicking types and touch selects.");
Assert(InputTools.Parse(1, InputTool.Text) == InputTool.Draw, "A saved tool must be read back.");
Assert(InputTools.Parse(9, InputTool.Select) == InputTool.Select && InputTools.Parse("Draw", InputTool.Text) == InputTool.Text,
    "An unknown saved tool falls back to the default.");

CultureInfo us = CultureInfo.GetCultureInfo("en-US"), uk = CultureInfo.GetCultureInfo("en-GB");
DateTime thursday = new(2026, 10, 22);
Note blank = NoteTemplates.Create(NoteTemplate.None, thursday, us);
Assert(blank.Template == NoteTemplate.None && blank.Title == Note.Untitled && blank.CanvasWidth == 400 && blank.CanvasHeight == 400 &&
    !NoteTemplates.Layout(NoteTemplate.None, 400, 400, us).Any(), "A blank page has no template, title, or lines.");
Note pink = NoteTemplates.Create(NoteTemplate.Lined, thursday, us, paper: NoteColor.Pink);
Assert(blank.Paper == NoteColor.Default && pink.Paper == NoteColor.Pink, "A page is made on the paper color asked for.");
Assert(JsonSerializer.Deserialize(JsonSerializer.Serialize(pink, NoteJsonContext.Default.Note), NoteJsonContext.Default.Note)?.Paper == NoteColor.Pink &&
    JsonSerializer.Deserialize("{}", NoteJsonContext.Default.Note)?.Paper == NoteColor.Default, "Paper color round-trips, and older notes have none.");
Note week = NoteTemplates.Create(NoteTemplate.Week, thursday, us);
Assert(week.Template == NoteTemplate.Week && week.Title == "Week of October 18" &&
    NoteTemplates.Create(NoteTemplate.Week, thursday, uk).Title == "Week of 19 October",
    "A week planner is named for its week, which starts on the region's first day.");
Assert(NoteTemplates.Layout(NoteTemplate.Week, 400, 416, uk).OfType<TemplateLabel>().Select(l => l.Text)
    .SequenceEqual(["Monday", "Tuesday", "Wednesday", "Thursday", "Friday", "Saturday", "Sunday", "Notes"]),
    "The week lists every day from the region's first day, then a notes cell.");
Note day = NoteTemplates.Create(NoteTemplate.Day, thursday, us);
string[] hours = NoteTemplates.Layout(NoteTemplate.Day, 400, day.CanvasHeight, uk).OfType<TemplateLabel>().Select(l => l.Text).ToArray();
Assert(day.Title == "Thursday, October 22, 2026" && hours.Length == 14 && hours[0] == "07:00" && hours[^1] == "20:00",
    "An hour planner is named for its day and lists 7 AM to 8 PM in the region's time format.");
foreach (Note planner in (Note[])[week, day])
{
    TemplateMark[] marks = NoteTemplates.Layout(planner.Template, planner.CanvasWidth, planner.CanvasHeight, us, planner.Options).ToArray();
    Assert(marks.OfType<TemplateLine>().All(l => Math.Max(l.X1, l.X2) <= planner.CanvasWidth && Math.Max(l.Y1, l.Y2) <= planner.CanvasHeight),
        "A planner's canvas is large enough for its whole layout.");
    Assert(NoteTemplates.Layout(planner.Template, 1200, 900, us, planner.Options).OfType<TemplateLabel>().SequenceEqual(marks.OfType<TemplateLabel>()),
        "A planner's days and hours stay put when the window grows, so writing stays where it was put.");
}
Assert(TemplateOptions.Parse(null) == new TemplateOptions() && TemplateOptions.Parse("{broken") == new TemplateOptions() &&
    TemplateOptions.Parse("{\"StartHour\":30}") == new TemplateOptions(), "Missing, corrupt, or impossible template settings fall back to the defaults.");
TemplateOptions chosen = new() { StartHour = 6, EndHour = 22, FirstDay = DayOfWeek.Monday, GraphOrigin = GraphOrigin.BottomLeft, GraphNumbers = 1 };
Assert(TemplateOptions.Parse(chosen.ToJson()) == chosen && !chosen.ToJson().Contains("IsValid"), "Template settings round-trip.");
Assert(week.Options?.FirstDay == DayOfWeek.Sunday, "A week page keeps the first day it was made with, even if Windows changes later.");
Note mondays = NoteTemplates.Create(NoteTemplate.Week, thursday, us, chosen);
Assert(mondays.Title == "Week of October 19" &&
    NoteTemplates.Layout(NoteTemplate.Week, 400, 416, us, mondays.Options).OfType<TemplateLabel>().First().Text == "Monday",
    "A chosen first day of the week overrides the region's.");
Note longDay = NoteTemplates.Create(NoteTemplate.Day, thursday, uk, chosen);
string[] longHours = NoteTemplates.Layout(NoteTemplate.Day, 400, longDay.CanvasHeight, uk, longDay.Options).OfType<TemplateLabel>().Select(l => l.Text).ToArray();
Assert(longHours.Length == 16 && longHours[0] == "06:00" && longHours[^1] == "21:00" && longDay.CanvasHeight == 480,
    "An hour planner runs over the chosen hours, with room for each.");
Assert(longDay.Options == new TemplateOptions { StartHour = 6, EndHour = 22 }, "A page keeps only the choices its layout uses.");
Assert(NoteTemplates.MinutesPerDivider(485) == 5 && NoteTemplates.MinutesPerDivider(161) == 10 && NoteTemplates.MinutesPerDivider(80.8) == 15 &&
    NoteTemplates.MinutesPerDivider(40.4) == 30 && NoteTemplates.MinutesPerDivider(34.6) == 0 && NoteTemplates.MinutesPerDivider(30) == 0,
    "Hours are divided as finely as still leaves room to write, and not at all when there is none.");
TemplateOptions sixHours = new() { HourRange = HourRange.FromNow, NextHours = 6 };
Note morning = NoteTemplates.Create(NoteTemplate.Day, new DateTime(2026, 10, 22, 9, 21, 0), uk, sixHours);
Assert(morning.Options == new TemplateOptions { StartHour = 9, EndHour = 15 }, "The next six hours from 9:21 start at 9:00.");
Note late = NoteTemplates.Create(NoteTemplate.Day, new DateTime(2026, 10, 22, 22, 5, 0), uk, sixHours with { NextHours = 4 });
Assert(late.Options is { IsValid: true } && NoteTemplates.Layout(NoteTemplate.Day, 400, late.CanvasHeight, uk, late.Options)
    .OfType<TemplateLabel>().Select(l => l.Text).SequenceEqual(["22:00", "23:00", "00:00", "01:00"]), "A page for the next few hours runs past midnight.");
TemplateOptions sixRows = NoteTemplates.FitHours(morning.Options!, 485.5);
TemplateMark[] sixRowMarks = NoteTemplates.Layout(NoteTemplate.Day, 400, NoteTemplates.DayHeight(sixRows), uk, sixRows).ToArray();
string[] sixRowLabels = sixRowMarks.OfType<TemplateLabel>().Select(l => l.Text).ToArray();
Assert(sixRows.HourHeight == 80.91 && NoteTemplates.DayHeight(sixRows) is > 485 and <= 485.5 &&
    sixRowMarks.OfType<TemplateLine>().All(l => Math.Max(l.Y1, l.Y2) <= 485.5) && sixRowLabels[0] == "09:00" &&
    sixRowLabels.Count(l => l == ":15") == 6 && sixRowLabels.Count(l => l == ":30") == 6 && sixRowLabels.Count(l => l == ":45") == 6,
    "Six hours fill the page between them, each split into quarter hours.");
Assert(sixRows.Unfitted == morning.Options && NoteTemplates.FitHours(new TemplateOptions { StartHour = 0, EndHour = 24 }, 300).HourHeight == 20,
    "Fitting only sets the row height, and never makes rows too short to write in.");
Assert(TemplateOptions.Parse("{\"NextHours\":0}") == new TemplateOptions() &&
    TemplateOptions.Parse(new TemplateOptions { StartHour = 22, EndHour = 30 }.ToJson()) == new TemplateOptions() &&
    TemplateOptions.Parse(sixHours.ToJson()) == sixHours, "Settings hold a fixed range within one day, or a count of hours from now.");
Note quadrant = NoteTemplates.Create(NoteTemplate.Graph, thursday, us, chosen);
TemplateMark[] firstQuadrant = NoteTemplates.Layout(NoteTemplate.Graph, quadrant.CanvasWidth, quadrant.CanvasHeight, us, quadrant.Options).ToArray();
int[] numbers = firstQuadrant.OfType<TemplateLabel>().Select(l => int.Parse(l.Text, us)).ToArray();
TemplateLine[] axes = firstQuadrant.OfType<TemplateLine>().Where(l => l.Thickness == 1.5).ToArray();
Assert(quadrant.CanvasHeight == 480 && axes.Length == 2 && axes.Any(l => l.Y1 == 440 && l.Y2 == 440) && axes.Any(l => l.X1 == 40 && l.X2 == 40) &&
    numbers.Min() == -1 && numbers.Max() == 21 && numbers.Count(n => n == 1) == 2,
    "Bottom left axes cross two squares in from the corner of the popup's page, numbered every square when chosen.");
TemplateMark[] plain = NoteTemplates.Layout(NoteTemplate.Graph, 400, 400, us, new TemplateOptions { GraphOrigin = GraphOrigin.None }).ToArray();
Assert(!plain.OfType<TemplateLabel>().Any() && plain.OfType<TemplateLine>().All(l => l.Thickness == 1) &&
    plain.OfType<TemplateLine>().Count(l => l.Strong) == 6, "Graph paper without axes has no numbers, only a bolder line every five squares.");
Assert(!NoteTemplates.Layout(NoteTemplate.Graph, 400, 400, us, new TemplateOptions { GraphNumbers = 0 }).OfType<TemplateLabel>().Any(),
    "Axes can go unnumbered.");
TemplateLine[] grid = NoteTemplates.Layout(NoteTemplate.Grid, 1000, 800, us).OfType<TemplateLine>().ToArray();
Assert(grid.Any(l => l.X1 > 950) && grid.Any(l => l.Y1 > 750) && grid.All(l => l.X2 <= 1000 && l.Y2 <= 800),
    "Paper fills the whole page and stops at its edge.");
Assert(NoteTemplates.Layout(NoteTemplate.Todo, 400, 400, us).OfType<TemplateBox>().Count() == 11, "Every whole to-do row has a box.");
Assert(NoteTemplates.Layout(NoteTemplate.Graph, 400, 400, us).OfType<TemplateLabel>().Select(l => l.Text).Order(StringComparer.Ordinal)
    .SequenceEqual(["-5", "-5", "0", "5", "5"]), "Graph axes are numbered every five squares from the origin.");

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
        InkBase64 = note.InkBase64, Thumbnail = "M1,2L3,4", Template = NoteTemplate.Todo
    };
    await store.SaveAsync(updated);
    string path = Path.Combine(folder, $"{note.Id:N}.json");
    string json = await File.ReadAllTextAsync(path);
    Assert(!json.Contains("\"Preview\""), "Derived preview must not be serialized.");
    Assert(json.Contains("\"Template\":\"Todo\""), "Templates are stored by name.");
    Assert(!File.Exists(path + ".tmp"), "Atomic save must finish with no pending temporary file.");
    store.Unload();
    await store.LoadAsync();
    Note restored = store.All.Single();
    Assert(restored.Title == updated.Title, "Replacement must survive reload.");
    Assert(restored.Template == NoteTemplate.Todo, "The template must survive reload.");
    Assert(restored.Texts.Single().X == 23.5 && restored.Texts.Single().Y == 75, "Object positions must survive reload.");
    Assert(restored.InkBase64 == note.InkBase64, "Original ink must survive reload.");
    Assert(restored.Thumbnail == "M1,2L3,4", "The thumbnail must survive reload.");
    Note doomed = new() { Title = "Delete me" };
    string doomedPath = Path.Combine(folder, $"{doomed.Id:N}.json");
    await store.SaveAsync(doomed);
    await store.DeleteAsync(doomed.Id);
    Assert(store.Find(doomed.Id) is null && !File.Exists(doomedPath) && store.All.Count == 1, "Deleting a note removes it and its file.");
    await store.SaveAsync(doomed);
    store.Unload();
    await store.SaveAsync(doomed);
    Assert(!File.Exists(doomedPath), "A save still under way for a deleted note must not bring it back, even after an unload.");
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
    foreach (string template in (string[])["\"Scribbles\"", "9"])
    {
        Note unknown = new();
        await File.WriteAllTextAsync(Path.Combine(folder, $"{unknown.Id:N}.json"),
            JsonSerializer.Serialize(unknown, NoteJsonContext.Default.Note).Replace("\"Template\":\"None\"", $"\"Template\":{template}"));
    }
    store.Unload();
    await store.LoadAsync();
    Assert(store.All.Count == 0 && store.LoadErrors.Count == 4, "Unknown templates must be reported, not guessed at.");
    Note planned = NoteTemplates.Create(NoteTemplate.Day, thursday, us, chosen);
    await File.WriteAllTextAsync(Path.Combine(folder, $"{planned.Id:N}.json"), JsonSerializer.Serialize(planned, NoteJsonContext.Default.Note));
    Note impossible = NoteTemplates.Create(NoteTemplate.Day, thursday, us);
    await File.WriteAllTextAsync(Path.Combine(folder, $"{impossible.Id:N}.json"),
        JsonSerializer.Serialize(impossible, NoteJsonContext.Default.Note).Replace("\"EndHour\":21", "\"EndHour\":5"));
    store.Unload();
    await store.LoadAsync();
    Assert(store.Find(planned.Id)?.Options == planned.Options && store.All.Count == 1 && store.LoadErrors.Count == 5,
        "A page's template choices survive reload, and impossible ones are reported.");

    Note legacy = new() { Title = "Before thumbnails", InkBase64 = note.InkBase64, Updated = now.AddDays(-30) };
    string legacyPath = Path.Combine(folder, $"{legacy.Id:N}.json");
    await File.WriteAllTextAsync(legacyPath, JsonSerializer.Serialize(legacy, NoteJsonContext.Default.Note)
        .Replace("\"Thumbnail\":\"\"", "\"RecognizedText\":\"Call Alice\",\"HandwritingKeywords\":\"Call Alicia\",\"IndexError\":\"\""));
    store.Unload();
    await store.LoadAsync();
    Assert(store.Find(legacy.Id) is { Thumbnail: "" } && !store.Find(legacy.Id)!.Matches("Alice"),
        "Notes saved with recognized handwriting still load, without it.");
    await store.AddThumbnailsAsync([(legacy.Id, "stale ink", "M0,0L1,1"), (legacy.Id, legacy.InkBase64, "M1,1L2,2")]);
    store.Unload();
    await store.LoadAsync();
    Assert(store.Find(legacy.Id) is { Thumbnail: "M1,1L2,2" } drawn && drawn.Updated == legacy.Updated,
        "A missing thumbnail is filled in from the ink it was drawn from, keeping the note's date.");

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
Console.WriteLine($"PASS: {assertions} search, thumbnail, stack, input tool, template, sticky layout, persistence and corruption assertions.");
