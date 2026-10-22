using System.Globalization;
using System.Numerics;
using System.Text;

namespace MiniInk;

// One stroke to draw: its points, its color as ARGB, and whether it was made with a highlighter.
public readonly record struct ThumbnailStroke(IReadOnlyList<Vector2> Points, uint Color = InkThumbnail.Black, bool Highlighter = false);

// The strokes of one color, as path markup.
public sealed record ThumbnailInk(string Path, uint Color, bool Highlighter)
{
    // Black and white pens are the ones that follow the page, so they are drawn in the page's ink
    // however the theme has changed since.
    public bool IsPageInk => !Highlighter && Color is InkThumbnail.Black or InkThumbnail.White;
}

// The ink in miniature for the library: a line drawing in path markup, kept with the note
// so the list never has to load ink to show it. It is a run of strokes for each color, each
// as `p` (pen) or `h` (highlighter), the color as eight hex digits of ARGB, a bar, and the path,
// with the runs joined by semicolons. Highlighters come first, so pen ink lies over them.
public static partial class InkThumbnail
{
    public const double Width = 64, Height = 48;
    public const uint Black = 0xFF000000, White = 0xFFFFFFFF;

    // Fits the strokes in the thumbnail, centered and never enlarged, dropping points too close together to show.
    public static string Create(IEnumerable<ThumbnailStroke> strokes)
    {
        ThumbnailStroke[] drawn = strokes.Where(s => s.Points.Count > 0).ToArray();
        if (drawn.Length == 0)
            return "";
        Vector2 min = new(float.MaxValue), max = new(float.MinValue);
        foreach (Vector2 point in drawn.SelectMany(s => s.Points))
        {
            min = Vector2.Min(min, point);
            max = Vector2.Max(max, point);
        }
        // A point of room on every side keeps round line ends inside.
        double scale = Math.Min(1, Math.Min((Width - 2) / Math.Max(max.X - min.X, 1), (Height - 2) / Math.Max(max.Y - min.Y, 1)));
        double left = (Width - (max.X - min.X) * scale) / 2, top = (Height - (max.Y - min.Y) * scale) / 2;
        return string.Join(';', drawn.GroupBy(s => (s.Color, s.Highlighter)).OrderBy(run => run.Key.Highlighter ? 0 : 1)
            .Select(run => $"{(run.Key.Highlighter ? 'h' : 'p')}{run.Key.Color:X8}|{Draw(run.Select(s => s.Points), min, left, top, scale)}"));
    }

    private static string Draw(IEnumerable<IReadOnlyList<Vector2>> strokes, Vector2 min, double left, double top, double scale)
    {
        StringBuilder path = new();
        foreach (IReadOnlyList<Vector2> stroke in strokes)
        {
            List<(double X, double Y)> points = [];
            for (int i = 0; i < stroke.Count; i++)
            {
                (double X, double Y) point = (left + (stroke[i].X - min.X) * scale, top + (stroke[i].Y - min.Y) * scale);
                if (points.Count == 0 || i == stroke.Count - 1 || Math.Abs(point.X - points[^1].X) + Math.Abs(point.Y - points[^1].Y) >= 1)
                    points.Add(point);
            }
            // A dot is a line of no length, drawn with round ends.
            if (points.Count == 1)
                points.Add(points[0]);
            path.Append('M').Append(Format(points[0])).Append('L').AppendJoin(' ', points.Skip(1).Select(Format));
        }
        return path.ToString();
    }

    // Thumbnails drawn before strokes kept their colors are a bare path, which is a single run of page ink.
    public static bool IsLegacy(string thumbnail) => thumbnail.StartsWith('M');

    // Notes without a thumbnail, or with a legacy one, get one drawn from their ink.
    public static bool NeedsDrawing(string thumbnail) => thumbnail.Length == 0 || IsLegacy(thumbnail);

    // A run that can't be read is left out rather than failing the list.
    public static List<ThumbnailInk> Parse(string thumbnail)
    {
        if (IsLegacy(thumbnail))
            return [new ThumbnailInk(thumbnail, Black, false)];
        List<ThumbnailInk> runs = [];
        foreach (string run in thumbnail.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            if (run.Length > 10 && (run[0] is 'p' or 'h') && run[9] == '|' &&
                uint.TryParse(run.AsSpan(1, 8), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint color))
                runs.Add(new ThumbnailInk(run[10..], color, run[0] == 'h'));
        }
        return runs;
    }

    private static string Format((double X, double Y) point) =>
        string.Create(CultureInfo.InvariantCulture, $"{point.X:0.#},{point.Y:0.#}");
}
