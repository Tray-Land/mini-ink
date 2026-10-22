using System.Text;

namespace MiniInk;

// A template's paper in miniature for the library, drawn behind the ink: faint rules and strong
// dividers as path markup, the way the editor tells them apart. It shows which paper a note is
// on, not where its ink sits, because the ink thumbnail is fitted to the tile rather than the page.
public sealed record TemplateSketch(string Faint, string Strong);

public static class TemplateThumbnail
{
    // The tile inside its border; the ink is drawn within it, InkThumbnail.Width by Height.
    public const int Width = 70, Height = 54;
    private const int Row = 9, TodoRow = 13, GraphCell = 6, Gutter = 18, Margin = 10, Box = 5;

    public static TemplateSketch Create(NoteTemplate template, TemplateOptions? options = null)
    {
        StringBuilder faint = new(), strong = new();
        switch (template)
        {
            case NoteTemplate.Week:
                for (int cell = 0; cell < 8; cell++)
                {
                    int x = cell / 4 * Width / 2, y = cell % 4 * Height / 4;
                    // A tick stands for the day's name, with a rule under it to write on.
                    Line(strong, x + 4, y + 4, x + 14, y + 4);
                    Line(faint, x + 4, y + 10, x + Width / 2 - 4, y + 10);
                }
                for (int row = 1; row < 4; row++)
                    Line(strong, 0, row * Height / 4, Width, row * Height / 4);
                Line(strong, Width / 2, 0, Width / 2, Height);
                break;
            case NoteTemplate.Day:
                for (int y = 0; y < Height; y += Row)
                {
                    // A tick in the gutter stands for the hour's label.
                    Line(strong, 4, y + 4, 14, y + 4);
                    if (y > 0)
                        Line(faint, 0, y, Width, y);
                }
                Line(strong, Gutter, 0, Gutter, Height);
                break;
            case NoteTemplate.Todo:
                for (int y = 0; y + TodoRow <= Height; y += TodoRow)
                {
                    strong.Append($"M4,{y + 4}h{Box}v{Box}h-{Box}z");
                    Line(faint, 0, y + TodoRow, Width, y + TodoRow);
                }
                break;
            case NoteTemplate.Lined:
                for (int y = Row; y < Height; y += Row)
                    Line(faint, 0, y, Width, y);
                Line(strong, Margin, 0, Margin, Height);
                break;
            case NoteTemplate.Grid:
                for (int x = Row; x < Width; x += Row)
                    Line(faint, x, 0, x, Height);
                for (int y = Row; y < Height; y += Row)
                    Line(faint, 0, y, Width, y);
                break;
            case NoteTemplate.Dots:
                // A dot is a line of no length, drawn with round ends.
                for (int y = Row; y < Height; y += Row)
                    for (int x = Row; x < Width; x += Row)
                        Line(strong, x, y, x, y);
                break;
            case NoteTemplate.Graph:
                // The axes cross where the page's do; a page made before there was a choice crosses at the center.
                (int X, int Y)? axes = (options?.GraphOrigin ?? GraphOrigin.Center) switch
                {
                    GraphOrigin.Center => (36, 24),
                    GraphOrigin.BottomLeft => (6, 48),
                    GraphOrigin.MiddleLeft => (6, 24),
                    _ => null
                };
                for (int x = GraphCell; x < Width; x += GraphCell)
                    if (x != axes?.X)
                        Line(faint, x, 0, x, Height);
                for (int y = GraphCell; y < Height; y += GraphCell)
                    if (y != axes?.Y)
                        Line(faint, 0, y, Width, y);
                if (axes is (int originX, int originY))
                {
                    Line(strong, 0, originY, Width, originY);
                    Line(strong, originX, 0, originX, Height);
                }
                break;
        }
        return new TemplateSketch(faint.ToString(), strong.ToString());
    }

    private static void Line(StringBuilder path, int x1, int y1, int x2, int y2) => path.Append($"M{x1},{y1}L{x2},{y2}");
}
