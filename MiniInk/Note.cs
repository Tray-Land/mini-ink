using System.Text.Json.Serialization;

namespace MiniInk;

public sealed class Note
{
    public const string Untitled = "Untitled note";
    public int FormatVersion { get; set; } = 1;
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Title { get; set; } = Untitled;
    public DateTimeOffset Updated { get; set; } = DateTimeOffset.Now;
    public List<TextNote> Texts { get; set; } = [];
    public string InkBase64 { get; set; } = "";
    // The ink as a line drawing for the library (see InkThumbnail); empty for notes saved before it was kept.
    public string Thumbnail { get; set; } = "";
    public int? StrokeCount { get; set; }
    public double CanvasWidth { get; set; } = 400;
    public double CanvasHeight { get; set; } = 400;
    public NoteTemplate Template { get; set; }
    // The planner or graph choices the page was made with; null on other pages.
    public TemplateOptions? Options { get; set; }
    [JsonIgnore]
    public string Preview => TypedText.Trim() is { Length: > 0 } text
        ? text : (StrokeCount is int count ? count == 0 : string.IsNullOrEmpty(InkBase64))
            ? "An empty page, ready for an idea" : "Ink note";
    [JsonIgnore]
    public string DateLabel => Updated.ToLocalTime().ToString("MMM d, h:mm tt");
    [JsonIgnore]
    private string TypedText => string.Join(" ", Texts.Select(t => t.Text));

    // Handwriting isn't read; a note is found by its title, typed text, or when it was last changed,
    // as the library shows it or written out in full.
    public bool Matches(string query)
    {
        string text = $"{Title} {TypedText} {DateLabel} {Updated.ToLocalTime():D}";
        return query.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
            .All(term => text.Contains(term, StringComparison.CurrentCultureIgnoreCase));
    }

    public override string ToString() => Title;
}

public sealed class TextNote
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Text { get; set; } = "";
    public double X { get; set; }
    public double Y { get; set; }
}

[JsonSerializable(typeof(Note))]
[JsonSerializable(typeof(TemplateOptions))]
internal partial class NoteJsonContext : JsonSerializerContext;
