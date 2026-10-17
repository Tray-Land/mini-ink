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
    public string RecognizedText { get; set; } = "";
    public string HandwritingKeywords { get; set; } = "";
    public string IndexError { get; set; } = "";
    public int? StrokeCount { get; set; }
    public double CanvasWidth { get; set; } = 400;
    public double CanvasHeight { get; set; } = 400;
    [JsonIgnore]
    public string Preview => SearchableText.Trim() is { Length: > 0 } text
        ? text : (StrokeCount is int count ? count == 0 : string.IsNullOrEmpty(InkBase64))
            ? "An empty page, ready for an idea" : "Ink note";
    [JsonIgnore]
    public string DateLabel => Updated.ToLocalTime().ToString("MMM d, h:mm tt");
    [JsonIgnore]
    private string SearchableText => string.Join(" ", Texts.Select(t => t.Text).Append(RecognizedText));

    public bool Matches(string query)
    {
        string text = $"{Title} {SearchableText} {HandwritingKeywords}";
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
internal partial class NoteJsonContext : JsonSerializerContext;
