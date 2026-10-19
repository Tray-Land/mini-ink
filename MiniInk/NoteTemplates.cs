using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MiniInk;

// The printed paper a page starts on. Its lines sit behind the ink and text rather than
// being part of them, so they are never erased, selected, moved, or drawn in thumbnails.
[JsonConverter(typeof(JsonStringEnumConverter<NoteTemplate>))]
public enum NoteTemplate
{
    None,
    Week,
    Day,
    Todo,
    Lined,
    Grid,
    Dots,
    Graph
}

// One mark of a template, in canvas coordinates. Strong lines are dividers, axes, and dots,
// which are sparse enough to need it; a line with DotSpacing is drawn as a row of dots that far apart.
public abstract record TemplateMark;
public sealed record TemplateLine(double X1, double Y1, double X2, double Y2,
    bool Strong = false, double Thickness = 1, double DotSpacing = 0) : TemplateMark;
public sealed record TemplateLabel(string Text, double X, double Y, double Size = 12, bool Bold = false) : TemplateMark;
public sealed record TemplateBox(double X, double Y, double Size) : TemplateMark;

// Where a graph's axes cross, on the page as it first opens.
[JsonConverter(typeof(JsonStringEnumConverter<GraphOrigin>))]
public enum GraphOrigin
{
    Center,
    BottomLeft,
    MiddleLeft,
    None
}

// Which hours a new hour planner shows: the same range every time, or the current hour and the ones after it.
[JsonConverter(typeof(JsonStringEnumConverter<HourRange>))]
public enum HourRange
{
    Fixed,
    FromNow
}

// The choices a new planner or graph page is made with, set in Settings. A page keeps the
// ones it was made with, so changing them never moves the lines under what is written there.
public sealed record TemplateOptions
{
    public HourRange HourRange { get; init; }
    public int StartHour { get; init; } = 7;
    // The hour the last row ends at; 24 is midnight, and a page made late in the day can run past it.
    public int EndHour { get; init; } = 21;
    public int NextHours { get; init; } = 6;
    // A page's row height, fitted to the window it opened in; null on pages made before rows were fitted.
    public double? HourHeight { get; init; }
    // Null follows the first day of the week Windows uses.
    public DayOfWeek? FirstDay { get; init; }
    public GraphOrigin GraphOrigin { get; init; }
    // How many squares apart the axis numbers are; 0 leaves them off.
    public int GraphNumbers { get; init; } = 5;
    public static IReadOnlyList<int> GraphNumberChoices { get; } = [5, 10, 1, 0];

    [JsonIgnore]
    public bool IsValid => Enum.IsDefined(HourRange) && StartHour is >= 0 and < 24 && EndHour > StartHour && EndHour <= StartHour + 24 &&
        NextHours is >= 1 and <= 24 && HourHeight is null or (>= 1 and <= 10000) &&
        (FirstDay is null || Enum.IsDefined(FirstDay.Value)) && Enum.IsDefined(GraphOrigin) && GraphNumberChoices.Contains(GraphNumbers);

    // The choices behind a page, leaving out the row height its window gave it.
    [JsonIgnore]
    public TemplateOptions Unfitted => this with { HourHeight = null };

    // Saved settings that can't be read, or that only a page could have, fall back to the defaults.
    public static TemplateOptions Parse(string? json)
    {
        try
        {
            return json is not null && JsonSerializer.Deserialize(json, NoteJsonContext.Default.TemplateOptions) is
                { IsValid: true, EndHour: <= 24, HourHeight: null } options ? options : new();
        }
        catch (JsonException)
        {
            return new();
        }
    }

    public string ToJson() => JsonSerializer.Serialize(this, NoteJsonContext.Default.TemplateOptions);
}

public static class NoteTemplates
{
    // Planners have a fixed layout, so what is written in a day or an hour stays there
    // however large the window gets. Paper repeats to fill the whole page.
    private const double WeekCellWidth = 200, WeekCellHeight = 104, WeekRuleSpacing = 30;
    // Hour rows are as tall as the page allows, never under MinHourHeight, and are split into the
    // finest of Increments that leaves MinIncrementSpacing to write in. LegacyHourHeight is the
    // fixed row height of pages made before rows were fitted.
    private const double LegacyHourHeight = 30, MinHourHeight = 20, MinIncrementSpacing = 20, HourGutter = 60;
    private static readonly int[] Increments = [5, 10, 15, 30];
    private const double TodoRow = 36, LineSpacing = 30, LineMargin = 44, GridSpacing = 24, GraphCell = 20;
    // Graph axes are placed two squares in from the edges of the page as it opens in the popup, about
    // 400 by 480. Centered axes stay where they were before there was a choice.
    private const double GraphCenter = 200, GraphNear = 40, GraphBottom = 440;

    public static Note Create(NoteTemplate template, DateTime now, CultureInfo culture, TemplateOptions? options = null)
    {
        options ??= new();
        Note note = new() { Template = template };
        DateTimeFormatInfo format = culture.DateTimeFormat;
        switch (template)
        {
            case NoteTemplate.Week:
                // The page keeps the first day it was made with, even if Windows later changes it.
                DayOfWeek first = options.FirstDay ?? format.FirstDayOfWeek;
                note.Options = new() { FirstDay = first };
                note.Title = $"Week of {WeekStart(now, first).ToString(format.MonthDayPattern, culture)}";
                note.CanvasHeight = WeekCellHeight * 4;
                break;
            case NoteTemplate.Day:
                // The page keeps the hours it was made for; its rows are fitted to the window it opens in.
                note.Options = options.HourRange == HourRange.FromNow
                    ? new() { StartHour = now.Hour, EndHour = now.Hour + options.NextHours }
                    : new() { StartHour = options.StartHour, EndHour = options.EndHour };
                note.Title = now.ToString(format.LongDatePattern, culture);
                note.CanvasHeight = DayHeight(note.Options);
                break;
            case NoteTemplate.Graph:
                note.Options = new() { GraphOrigin = options.GraphOrigin, GraphNumbers = options.GraphNumbers };
                // Tall enough for the bottom axis and its numbers in a smaller window too.
                if (options.GraphOrigin == GraphOrigin.BottomLeft)
                    note.CanvasHeight = GraphBottom + 2 * GraphCell;
                break;
        }
        return note;
    }

    public static DateTime WeekStart(DateTime day, DayOfWeek firstDay) =>
        day.Date.AddDays(-(((int)day.DayOfWeek - (int)firstDay + 7) % 7));

    public static string HourLabel(int hour, CultureInfo culture) => new DateTime(2000, 1, 1).AddHours(hour).ToString("t", culture);

    // Shares a page's height between its hours, never making a row too short to write in.
    public static TemplateOptions FitHours(TemplateOptions options, double pageHeight) => options with
    {
        HourHeight = Math.Max(MinHourHeight, Math.Floor(pageHeight / (options.EndHour - options.StartHour) * 100) / 100)
    };

    public static double DayHeight(TemplateOptions options) => (options.HourHeight ?? LegacyHourHeight) * (options.EndHour - options.StartHour);

    // How many minutes apart an hour's dividers are, or 0 when there is no room for any.
    public static int MinutesPerDivider(double hourHeight) => Increments.FirstOrDefault(minutes => hourHeight * minutes / 60 >= MinIncrementSpacing);

    // Pages made before there were options are drawn with the defaults.
    public static IEnumerable<TemplateMark> Layout(NoteTemplate template, double width, double height, CultureInfo culture,
        TemplateOptions? options = null) =>
        template switch
        {
            NoteTemplate.Week => Week(culture, options ?? new()),
            NoteTemplate.Day => Day(width, culture, options ?? new()),
            NoteTemplate.Todo => Todo(width, height),
            NoteTemplate.Lined => Lined(width, height),
            NoteTemplate.Grid => Grid(width, height),
            NoteTemplate.Dots => Dots(width, height),
            NoteTemplate.Graph => Graph(width, height, culture, options ?? new()),
            _ => []
        };

    // Two columns of days, as in a paper planner, with room for notes after the last day.
    private static IEnumerable<TemplateMark> Week(CultureInfo culture, TemplateOptions options)
    {
        DateTimeFormatInfo format = culture.DateTimeFormat;
        DayOfWeek first = options.FirstDay ?? format.FirstDayOfWeek;
        for (int cell = 0; cell < 8; cell++)
        {
            double x = cell / 4 * WeekCellWidth, y = cell % 4 * WeekCellHeight;
            string name = cell < 7 ? format.GetDayName((DayOfWeek)(((int)first + cell) % 7)) : "Notes";
            yield return new TemplateLabel(name, x + 10, y + 8, 13, Bold: true);
            for (double rule = y + WeekCellHeight - WeekRuleSpacing; rule > y + 36; rule -= WeekRuleSpacing)
                yield return new TemplateLine(x + 10, rule, x + WeekCellWidth - 10, rule);
        }
        for (int row = 1; row <= 4; row++)
            yield return new TemplateLine(0, row * WeekCellHeight, WeekCellWidth * 2, row * WeekCellHeight, Strong: true);
        yield return new TemplateLine(WeekCellWidth, 0, WeekCellWidth, WeekCellHeight * 4, Strong: true);
    }

    // A row per hour, labeled in the region's time format, with dotted dividers between the hours
    // when they are tall enough. Lines sit on whole pixels so fitted rows stay crisp.
    private static IEnumerable<TemplateMark> Day(double width, CultureInfo culture, TemplateOptions options)
    {
        double hourHeight = options.HourHeight ?? LegacyHourHeight;
        int minutes = MinutesPerDivider(hourHeight);
        double step = hourHeight * minutes / 60;
        // Each label is centered in the first band of its hour.
        double band = minutes > 0 ? step : hourHeight;
        for (int hour = options.StartHour; hour < options.EndHour; hour++)
        {
            double top = (hour - options.StartHour) * hourHeight;
            yield return new TemplateLabel(HourLabel(hour, culture), 8, Math.Round(top + Math.Max(2, (band - 16) / 2)), 11);
            for (int minute = minutes; minutes > 0 && minute < 60; minute += minutes)
            {
                double y = Math.Round(top + step * minute / minutes);
                yield return new TemplateLine(HourGutter, y, width, y, Strong: true, Thickness: 1.5, DotSpacing: 6);
                yield return new TemplateLabel($":{minute:00}", 8, Math.Round(y + Math.Max(2, (step - 13) / 2)), 9);
            }
            double bottom = Math.Round(top + hourHeight);
            yield return new TemplateLine(0, bottom, width, bottom);
        }
        yield return new TemplateLine(HourGutter, 0, HourGutter, Math.Round(DayHeight(options)), Strong: true);
    }

    private static IEnumerable<TemplateMark> Todo(double width, double height)
    {
        for (double y = 0; y + TodoRow <= height; y += TodoRow)
        {
            yield return new TemplateBox(14, y + 10, 16);
            yield return new TemplateLine(0, y + TodoRow, width, y + TodoRow);
        }
    }

    private static IEnumerable<TemplateMark> Lined(double width, double height)
    {
        for (double y = LineSpacing; y <= height; y += LineSpacing)
            yield return new TemplateLine(0, y, width, y);
        yield return new TemplateLine(LineMargin, 0, LineMargin, height, Strong: true);
    }

    private static IEnumerable<TemplateMark> Grid(double width, double height)
    {
        for (double x = GridSpacing; x < width; x += GridSpacing)
            yield return new TemplateLine(x, 0, x, height);
        for (double y = GridSpacing; y < height; y += GridSpacing)
            yield return new TemplateLine(0, y, width, y);
    }

    private static IEnumerable<TemplateMark> Dots(double width, double height)
    {
        for (double y = GridSpacing; y < height; y += GridSpacing)
            yield return new TemplateLine(GridSpacing, y, width, y, Strong: true, Thickness: 2.5, DotSpacing: GridSpacing);
    }

    // Graph paper: a bolder line every five squares counted from the axes, which are numbered
    // as often as chosen. Without axes the squares count from the top left corner.
    private static IEnumerable<TemplateMark> Graph(double width, double height, CultureInfo culture, TemplateOptions options)
    {
        (double X, double Y)? axes = options.GraphOrigin switch
        {
            GraphOrigin.Center => (GraphCenter, GraphCenter),
            GraphOrigin.BottomLeft => (GraphNear, GraphBottom),
            GraphOrigin.MiddleLeft => (GraphNear, GraphCenter),
            _ => null
        };
        (double originX, double originY) = axes ?? (0, 0);
        int every = axes is null ? 0 : options.GraphNumbers;
        for (double x = GraphCell; x < width; x += GraphCell)
        {
            int units = (int)Math.Round((x - originX) / GraphCell);
            if (units != 0 || axes is null)
                yield return new TemplateLine(x, 0, x, height, Strong: units % 5 == 0);
            if (every > 0 && units % every == 0)
                yield return new TemplateLabel(units.ToString(culture), x + 3, originY + 2, 10);
        }
        for (double y = GraphCell; y < height; y += GraphCell)
        {
            int units = (int)Math.Round((originY - y) / GraphCell);
            if (units != 0 || axes is null)
                yield return new TemplateLine(0, y, width, y, Strong: units % 5 == 0);
            if (every > 0 && units % every == 0 && units != 0)
                yield return new TemplateLabel(units.ToString(culture), originX + 3, y + 2, 10);
        }
        if (axes is null)
            yield break;
        yield return new TemplateLine(0, originY, width, originY, Strong: true, Thickness: 1.5);
        yield return new TemplateLine(originX, 0, originX, height, Strong: true, Thickness: 1.5);
    }
}
