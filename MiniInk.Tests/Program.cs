using System.Globalization;
using System.Numerics;
using System.Text.Json;
using System.Text.RegularExpressions;
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

Assert(InkThumbnail.Create([]) == "" && InkThumbnail.Create([new ThumbnailStroke([])]) == "", "No ink draws no thumbnail.");
Vector2[] line = Enumerable.Range(0, 201).Select(x => new Vector2(100 + x, 100)).ToArray();
string sketch = InkThumbnail.Create([new ThumbnailStroke(line), new ThumbnailStroke([new Vector2(200, 140)])]);
Assert(sketch.StartsWith("pFF000000|M1,17.8L") && sketch.EndsWith("63,17.8M32,30.2L32,30.2"),
    "Large ink is scaled to fit the thumbnail, centered, with a dot drawn as a line of no length.");
Assert(sketch.Split(' ').Length < 80, "Points too close together to show are dropped.");
Assert(InkThumbnail.Create([new ThumbnailStroke([new Vector2(500, 500), new Vector2(510, 510)])]) == "pFF000000|M27,19L37,29", "Small ink keeps its size, centered.");

const uint Red = 0xFFE61B1B, Yellow = 0xFFFFE600;
Vector2[] diagonal = [new(110, 105), new(130, 130)];
string colored = InkThumbnail.Create([new ThumbnailStroke(line, Red), new ThumbnailStroke(diagonal), new ThumbnailStroke(line, Yellow, Highlighter: true),
    new ThumbnailStroke(diagonal, Red)]);
ThumbnailInk[] runs = [.. InkThumbnail.Parse(colored)];
Assert(runs.Select(r => (r.Color, r.Highlighter)).SequenceEqual([(Yellow, true), (Red, false), (InkThumbnail.Black, false)]),
    "Ink is drawn a run for each color, with highlighters first so pen ink lies over them.");
Assert(runs[1].Path.Count(c => c == 'M') == 2 && runs[2].Path.Count(c => c == 'M') == 1 && runs.All(r => r.Path.StartsWith('M')),
    "Strokes of a color share a run, in the order they were drawn, and are fitted to the thumbnail together.");
Assert(runs[0].Path.Split('L')[0] == runs[1].Path.Split('L')[0] && runs[0].Path.StartsWith("M1,") && runs[2].Path.Split('L')[0] != runs[0].Path.Split('L')[0],
    "Every color is placed by the same fit, so the colors line up as they were drawn.");
Assert(InkThumbnail.Parse(colored).Select(r => r.IsPageInk).SequenceEqual([false, false, true]) &&
    new ThumbnailInk("M0,0L1,1", InkThumbnail.White, false).IsPageInk && !new ThumbnailInk("M0,0L1,1", InkThumbnail.Black, true).IsPageInk,
    "Black and white pens are the page's ink; colors, and highlighters of any color, are drawn as they were.");
Assert(InkThumbnail.IsLegacy("M1,2L3,4") && !InkThumbnail.IsLegacy(colored) && !InkThumbnail.IsLegacy("") &&
    InkThumbnail.NeedsDrawing("") && InkThumbnail.NeedsDrawing("M1,2L3,4") && !InkThumbnail.NeedsDrawing(colored),
    "A note needs a thumbnail drawn when it has none, or one from before ink kept its colors.");
Assert(InkThumbnail.Parse("M1,2L3,4").Single() == new ThumbnailInk("M1,2L3,4", InkThumbnail.Black, false) && InkThumbnail.Parse("").Count == 0,
    "A legacy thumbnail is one run of page ink.");
Assert(InkThumbnail.Parse("pZZZZZZZZ|M1,1;q00000000|M1,1;pFF000000M1,1;pFF000000|;" + "hFF00AACC|M2,2L3,3").Single() ==
    new ThumbnailInk("M2,2L3,3", 0xFF00AACC, true), "Runs that can't be read are left out, not guessed at.");

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

Assert(PopupSize.Width(null) == PopupSize.DefaultWidth && PopupSize.Height(null) == PopupSize.DefaultHeight &&
    PopupSize.Width("wide") == PopupSize.DefaultWidth && PopupSize.Height(6.5) == PopupSize.DefaultHeight,
    "With no usable saved size the popup opens at its default size.");
Assert(PopupSize.Width(520) == 520 && PopupSize.Height(700) == 700, "A saved size must be read back.");
Assert(PopupSize.Width(10) == PopupSize.MinWidth && PopupSize.Height(10) == PopupSize.MinHeight &&
    PopupSize.Width(99999) == PopupSize.MaxWidth && PopupSize.Height(99999) == PopupSize.MaxHeight,
    "A saved size too small to use, or too large to show, is held to the limits.");
Assert(PopupSize.Width(PopupSize.DefaultWidth) == PopupSize.DefaultWidth && PopupSize.Height(PopupSize.DefaultHeight) == PopupSize.DefaultHeight,
    "The default size must be within the limits.");
Assert(PopupSize.FromPixels(440, 96) == 440 && PopupSize.FromPixels(550, 120) == 440 && PopupSize.FromPixels(660, 144) == 440 &&
    PopupSize.FromPixels(1240, 200) == 595 && PopupSize.FromPixels(440, 0) == 440,
    "A window's pixels become the logical size it is saved as, at any display scale.");

PenSet pens = PenSet.Default;
Assert(pens.Pens.Select(pen => pen.Kind).SequenceEqual(PenKinds.All) && pens.IsValid && pens.Active == pens.Pens[0],
    "A new install starts with one of each pen, the first in hand.");
PenSet withSecond = pens.Add(PenKind.Ballpoint);
Assert(withSecond.Pens.Length == 4 && withSecond.Label(withSecond.Pens[3]) == "Pen 2" && withSecond.Label(withSecond.Pens[0]) == "Pen",
    "A second pen of a kind is added at the end, and numbered.");
PenSet removed = withSecond.Remove(withSecond.Pens[0].Id);
Assert(removed.Pens.Select(pen => pen.Kind).SequenceEqual([PenKind.Pencil, PenKind.Highlighter, PenKind.Ballpoint]) &&
    removed.Pens[2].Id == withSecond.Pens[3].Id && removed.Label(removed.Pens[2]) == "Pen",
    "Removing a pen leaves the rest where they were, and the next of its kind is no longer numbered.");
PenSet missing = pens.Remove(pens.Pens[0].Id).Remove(pens.Pens[1].Id);
Assert(missing.Pens.Length == 1 && missing.Add(PenKind.Ballpoint).Pens.Select(pen => pen.Kind).SequenceEqual([PenKind.Highlighter, PenKind.Ballpoint]),
    "A pen goes on the end, whatever its kind.");
PenSet full = withSecond.Add(PenKind.Pencil);
Assert(full.Pens.Length == PenSet.MaxPens && !full.CanAdd && ReferenceEquals(full.Add(PenKind.Pencil), full), "A toolbar holds only so many pens.");
PenSet down = full.Activate(full.Pens[2].Id).Remove(full.Pens[2].Id);
Assert(down.Pens.Length == 4 && down.Active == down.Pens[0], "Removing the pen in hand picks up the first.");
Assert(down.Remove(down.Pens[1].Id).Active == down.Pens[0] && down.Remove(down.Pens[0].Id).Pens.Length == 3,
    "Removing another pen leaves the rest in order.");
PenSet lone = pens.Remove(pens.Pens[1].Id).Remove(pens.Pens[2].Id);
Assert(lone.Pens.Length == 1 && !lone.CanRemove && ReferenceEquals(lone.Remove(lone.Pens[0].Id), lone), "The last pen can't be removed.");
PenSpec red = pens.Pens[0] with { Color = 0xFFE81123, Size = 4 };
PenSet recolored = pens.Replace(red);
Assert(recolored.Pens[0] == red && recolored.SameTools(pens) && !recolored.SameAs(pens) && ReferenceEquals(recolored.Replace(red), recolored),
    "Recoloring a pen changes it in place without changing the tools.");
Assert(!full.SameTools(pens) && pens.SameAs(pens) && !pens.Activate(pens.Pens[1].Id).SameAs(pens), "Adding a pen changes the tools; picking one up does not.");

string[] ids = [.. full.Pens.Select(pen => pen.Id)];
PenSet moved = full.Activate(ids[3]).Move(ids[0], 2);
Assert(moved.Pens.Select(pen => pen.Id).SequenceEqual([ids[1], ids[2], ids[0], ids[3], ids[4]]) && moved.IsValid && moved.Active.Id == ids[3] &&
    moved.Move(ids[0], moved.IndexOf(ids[0]) - 1).Pens.Select(pen => pen.Id).SequenceEqual([ids[1], ids[0], ids[2], ids[3], ids[4]]) &&
    moved.Move(ids[4], 0).Pens[0].Id == ids[4],
    "A pen moves to any place, one step at a time or in one go, and the rest keep their order and the pen in hand.");
Assert(ReferenceEquals(full.Move(ids[0], -1), full) && ReferenceEquals(full.Move(ids[4], 5), full) && ReferenceEquals(full.Move(ids[2], 2), full) &&
    ReferenceEquals(full.Move("not a pen", 1), full) && !moved.SameTools(full) && !moved.SameAs(full),
    "Moving a pen past either end, to where it is, or a pen that is not there, changes nothing; moving one anywhere else changes the tools.");
PenSet renumbered = withSecond.Move(withSecond.Pens[3].Id, 0);
Assert(renumbered.Label(renumbered.Pens[0]) == "Pen" && renumbered.Label(renumbered.Pens[1]) == "Pen 2" && renumbered.Pens[0].Id == withSecond.Pens[3].Id,
    "Pens of a kind are numbered in the order they are in.");
Assert(PenSet.Parse(moved.ToJson()).Pens.Select(pen => pen.Id).SequenceEqual(moved.Pens.Select(pen => pen.Id)),
    "The order the pens were put in is remembered, not put back in the order of their kinds.");

PenSet kept = PenSet.Parse(pens.Replace(red).Add(PenKind.Highlighter).Activate(pens.Pens[1].Id).ToJson());
Assert(kept.Pens.Length == 4 && kept.Pens[0] == red && kept.Active.Kind == PenKind.Pencil && kept.Pens[3].Color is null,
    "Pens keep their color, size and order, and the one in hand, through saving.");
Assert(PenSet.Parse(null).Pens.Length == 3 && PenSet.Parse("{broken").Pens.Length == 3 && PenSet.Parse("{\"Pens\":[]}").Pens.Length == 3 &&
    PenSet.Parse("{\"Pens\":[{\"Id\":\"a\",\"Kind\":\"Laser\"}]}").Pens.Length == 3 &&
    PenSet.Parse("{\"Pens\":[{\"Id\":\"a\",\"Kind\":\"Pencil\",\"Size\":-1}]}").Pens.Length == 3 &&
    PenSet.Parse("{\"Pens\":[{\"Id\":\"a\",\"Kind\":\"Pencil\"},{\"Id\":\"a\",\"Kind\":\"Pencil\"}]}").Pens.Length == 3,
    "Saved pens that can't be used fall back to the defaults.");
Assert(PenSet.Parse("{\"Pens\":[{\"Id\":\"a\",\"Kind\":\"Pencil\"}],\"ActiveId\":\"gone\"}").Active.Id == "a",
    "A pen in hand that no longer exists is replaced by the first.");

string? remembered = null;
List<PenChange> changes = [];
PenStore session = new(() => remembered, json => remembered = json);
session.Changed += (_, change) => changes.Add(change);
Assert(session.Current.Pens.Select(pen => pen.Kind).SequenceEqual(PenKinds.All) && remembered is null, "With nothing saved, the first session starts on the defaults without writing.");
object window = new();
session.Edit(set => set.Replace(set.Pens[0] with { Color = 0xFF0063B1, Size = 3.5 }), window);
session.Edit(set => set.Replace(set.Pens[0] with { Color = 0xFF0063B1, Size = 3.5 }), window);
Assert(changes.Count == 1 && !changes[0].ToolsChanged && ReferenceEquals(changes[0].Source, window) && remembered is not null,
    "A pen recolored is saved once and reported as a repaint from the toolbar that did it.");
session.Edit(set => set.Add(PenKind.Pencil));
session.Edit(set => set.Activate(set.Pens[3].Id), window);
Assert(changes.Count == 3 && changes[1].ToolsChanged && changes[1].Source is null && !changes[2].ToolsChanged, "Adding a pen is reported as new tools.");
PenStore nextSession = new(() => remembered, json => remembered = json);
Assert(nextSession.Current.SameAs(session.Current) && nextSession.Current.Pens[0].Color == 0xFF0063B1 && nextSession.Current.Pens[0].Size == 3.5 &&
    nextSession.Current.Active.Kind == PenKind.Pencil && nextSession.Current.Pens.Length == 4,
    "The next session starts with the same pens, colors, sizes and pen in hand.");
session.Edit(set => set.Remove("not a pen"));
session.Edit(_ => new PenSet());
Assert(changes.Count == 3, "Edits that change nothing, or would leave no pens, are ignored.");
session.Reset();
Assert(changes.Count == 4 && changes[3].ToolsChanged && PenSet.Parse(remembered).Pens.Length == 3 && PenSet.Parse(remembered).Pens[0].Color is null,
    "Resetting puts back the default pens.");
string firstId = session.Current.Pens[0].Id;
session.Edit(set => set.Move(firstId, 1));
Assert(changes.Count == 5 && changes[4].ToolsChanged && session.Current.Pens[1].Id == firstId && PenSet.Parse(remembered).Pens[1].Id == firstId,
    "Moving a pen is reported as new tools and remembered.");

CultureInfo us = CultureInfo.GetCultureInfo("en-US"), uk = CultureInfo.GetCultureInfo("en-GB");
DateTime thursday = new(2026, 10, 22);
Note blank = NoteTemplates.Create(NoteTemplate.None, thursday, us);
Assert(blank.Template == NoteTemplate.None && blank.Title == Note.Untitled && blank.CanvasWidth == 400 && blank.CanvasHeight == 400 &&
    !NoteTemplates.Layout(NoteTemplate.None, 400, 400, us).Any(), "A blank page has no template, title, or lines.");
Note pink = NoteTemplates.Create(NoteTemplate.Lined, thursday, us, paper: NoteColor.Pink);
Assert(blank.Paper == NoteColor.Default && pink.Paper == NoteColor.Pink, "A page is made on the paper color asked for.");
Assert(JsonSerializer.Deserialize(JsonSerializer.Serialize(pink, NoteJsonContext.Default.Note), NoteJsonContext.Default.Note)?.Paper == NoteColor.Pink &&
    JsonSerializer.Deserialize("{}", NoteJsonContext.Default.Note)?.Paper == NoteColor.Default, "Paper color round-trips, and older notes have none.");
TemplateSketch[] papers = Enum.GetValues<NoteTemplate>().Where(t => t != NoteTemplate.None).Select(t => TemplateThumbnail.Create(t)).ToArray();
Assert(TemplateThumbnail.Create(NoteTemplate.None) == new TemplateSketch("", "") && papers.All(p => p.Faint.Length + p.Strong.Length > 0),
    "A blank page has no paper in its thumbnail, and every template has some.");
Assert(papers.Select(p => (p.Faint, p.Strong)).Distinct().Count() == papers.Length, "Each template's paper looks different in the library.");
Assert(papers.All(p => Regex.Matches(p.Faint + p.Strong, @"\d+").All(n => int.Parse(n.Value) <= TemplateThumbnail.Width)),
    "A thumbnail's paper stays within its tile.");
Assert(TemplateThumbnail.Create(NoteTemplate.Dots).Faint == "" && TemplateThumbnail.Create(NoteTemplate.Lined).Strong.Length > 0 &&
    TemplateThumbnail.Create(NoteTemplate.Grid).Strong == "", "Dots and dividers are strong marks, and rules are faint ones.");
TemplateSketch[] graphs = Enum.GetValues<GraphOrigin>().Select(o => TemplateThumbnail.Create(NoteTemplate.Graph, new TemplateOptions { GraphOrigin = o })).ToArray();
Assert(graphs.Select(g => g.Strong).Distinct().Count() == 4 && graphs[3].Strong == "" && TemplateThumbnail.Create(NoteTemplate.Graph) == graphs[0],
    "A graph's axes cross where the page's do, are left off when it has none, and cross at the center for pages made before the choice.");
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
    await store.AddThumbnailsAsync([(legacy.Id, "stale ink", "pFF000000|M0,0L1,1"), (legacy.Id, legacy.InkBase64, "pFF000000|M1,1L2,2")]);
    store.Unload();
    await store.LoadAsync();
    Assert(store.Find(legacy.Id) is { Thumbnail: "pFF000000|M1,1L2,2" } drawn && drawn.Updated == legacy.Updated,
        "A missing thumbnail is filled in from the ink it was drawn from, keeping the note's date.");
    await store.AddThumbnailsAsync([(legacy.Id, legacy.InkBase64, "pFFE61B1B|M9,9L9,9")]);
    Assert(store.Find(legacy.Id)?.Thumbnail == "pFF000000|M1,1L2,2", "A thumbnail that already has its colors is not drawn again.");
    Note faded = new() { Title = "Before colors", InkBase64 = note.InkBase64, Thumbnail = "M3,3L4,4", Updated = now.AddDays(-10) };
    await File.WriteAllTextAsync(Path.Combine(folder, $"{faded.Id:N}.json"), JsonSerializer.Serialize(faded, NoteJsonContext.Default.Note));
    store.Unload();
    await store.LoadAsync();
    await store.AddThumbnailsAsync([(faded.Id, faded.InkBase64, "pFFE61B1B|M5,5L6,6")]);
    store.Unload();
    await store.LoadAsync();
    Assert(store.Find(faded.Id) is { Thumbnail: "pFFE61B1B|M5,5L6,6" } colored2 && colored2.Updated == faded.Updated,
        "A thumbnail drawn before ink kept its colors is drawn again, keeping the note's date.");

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
Console.WriteLine($"PASS: {assertions} search, thumbnail, stack, input tool, popup size, pen, template, sticky layout, persistence and corruption assertions.");
