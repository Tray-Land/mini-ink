using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;
using Windows.UI;

namespace MiniInk;

// Sticky note colors are the same bright shades in either theme; a colored page is always a light
// page, so its ink and text turn dark even when the app is dark.
public static class NoteColors
{
    public static NoteColor[] All { get; } = Enum.GetValues<NoteColor>();

    public static string Label(NoteColor color) => color == NoteColor.Default ? "Theme default" : color.ToString();

    // Null for Default, which is the theme's own page color.
    public static Color? Paper(NoteColor color) => color switch
    {
        NoteColor.Yellow => Color.FromArgb(255, 0xFF, 0xF3, 0x5C),
        NoteColor.Pink => Color.FromArgb(255, 0xFF, 0x7E, 0xB6),
        NoteColor.Green => Color.FromArgb(255, 0x86, 0xF0, 0x6F),
        NoteColor.Blue => Color.FromArgb(255, 0x5C, 0xCB, 0xFF),
        NoteColor.Orange => Color.FromArgb(255, 0xFF, 0xA0, 0x40),
        NoteColor.Purple => Color.FromArgb(255, 0xC4, 0x8B, 0xFF),
        _ => null
    };
}

// The menu of paper colors behind the editor's color button.
public static class PaperMenu
{
    public static MenuFlyout Create(Func<NoteColor> current, Action<NoteColor> pick)
    {
        MenuFlyout menu = new();
        List<(NoteColor Color, RadioMenuFlyoutItem Item)> items = [];
        foreach (NoteColor color in NoteColors.All)
        {
            RadioMenuFlyoutItem item = new() { Text = NoteColors.Label(color), GroupName = "Paper", Icon = Swatch(NoteColors.Paper(color)) };
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(item, $"Paper{color}");
            item.Click += (_, _) => pick(color);
            items.Add((color, item));
            menu.Items.Add(item);
        }
        // The check follows the page's color, so it is set each time the menu opens.
        menu.Opening += (_, _) =>
        {
            foreach ((NoteColor color, RadioMenuFlyoutItem item) in items)
                item.IsChecked = color == current();
        };
        return menu;
    }

    private static PathIcon Swatch(Color? paper) => new()
    {
        Data = new EllipseGeometry { Center = new Point(8, 8), RadiusX = 7, RadiusY = 7 },
        Foreground = paper is Color fill ? new SolidColorBrush(fill) : (Brush)Application.Current.Resources["TextFillColorDisabledBrush"]
    };
}
