using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;

namespace MiniInk;

// The menu behind each New page split button: a blank page, the planners, then plain paper.
public static class TemplateMenu
{
    private static readonly (NoteTemplate Template, string Label, string? Glyph)[][] Groups =
    [
        [(NoteTemplate.None, "Blank page", "")],
        [(NoteTemplate.Week, "Day by day", ""), (NoteTemplate.Day, "Hour by hour", ""),
            (NoteTemplate.Todo, "To-do list", "")],
        // Segoe has no dot grid glyph, so that one is drawn.
        [(NoteTemplate.Lined, "Lined", ""), (NoteTemplate.Grid, "Grid", ""), (NoteTemplate.Dots, "Dot grid", null),
            (NoteTemplate.Graph, "Graph", "")]
    ];

    public static string Label(NoteTemplate template) =>
        Groups.SelectMany(group => group).First(entry => entry.Template == template).Label;

    // Each split button needs its own menu.
    public static MenuFlyout Create()
    {
        MenuFlyout menu = new();
        foreach (var group in Groups)
        {
            if (menu.Items.Count > 0)
                menu.Items.Add(new MenuFlyoutSeparator());
            foreach ((NoteTemplate template, string label, string? glyph) in group)
            {
                MenuFlyoutItem item = new() { Text = label, Icon = glyph is null ? DotGridIcon() : new FontIcon { Glyph = glyph } };
                if (template == NoteTemplate.None)
                    item.KeyboardAcceleratorTextOverride = "Ctrl+N";
                AutomationProperties.SetAutomationId(item, $"Template{template}");
                item.Click += async (_, _) => await App.Current.CreateNoteAsync(template);
                menu.Items.Add(item);
            }
        }
        return menu;
    }

    private static PathIcon DotGridIcon()
    {
        GeometryGroup dots = new();
        for (int row = 0; row < 3; row++)
        {
            for (int column = 0; column < 3; column++)
                dots.Children.Add(new EllipseGeometry { Center = new Point(3 + column * 5, 3 + row * 5), RadiusX = 1.5, RadiusY = 1.5 });
        }
        return new PathIcon { Data = dots };
    }
}
