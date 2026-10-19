using System.Globalization;
using System.Numerics;
using System.Text;

namespace MiniInk;

// The ink in miniature for the library: a line drawing in path markup, kept with the note
// so the list never has to load ink to show it.
public static partial class InkThumbnail
{
    public const double Width = 64, Height = 48;

    // Fits the strokes in the thumbnail, centered and never enlarged, dropping points too close together to show.
    public static string Create(IEnumerable<IReadOnlyList<Vector2>> strokes)
    {
        IReadOnlyList<Vector2>[] drawn = strokes.Where(s => s.Count > 0).ToArray();
        if (drawn.Length == 0)
            return "";
        Vector2 min = new(float.MaxValue), max = new(float.MinValue);
        foreach (Vector2 point in drawn.SelectMany(s => s))
        {
            min = Vector2.Min(min, point);
            max = Vector2.Max(max, point);
        }
        // A point of room on every side keeps round line ends inside.
        double scale = Math.Min(1, Math.Min((Width - 2) / Math.Max(max.X - min.X, 1), (Height - 2) / Math.Max(max.Y - min.Y, 1)));
        double left = (Width - (max.X - min.X) * scale) / 2, top = (Height - (max.Y - min.Y) * scale) / 2;
        StringBuilder path = new();
        foreach (IReadOnlyList<Vector2> stroke in drawn)
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

    private static string Format((double X, double Y) point) =>
        string.Create(CultureInfo.InvariantCulture, $"{point.X:0.#},{point.Y:0.#}");
}
