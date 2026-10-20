using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;
using Windows.UI;

namespace MiniInk;

// Sticky note colors come in a bright shade for the light theme and a deeper one for the dark theme.
public static class NoteColors
{
    public static NoteColor[] All { get; } = Enum.GetValues<NoteColor>();

    public static string Label(NoteColor color) => color == NoteColor.Default ? "Theme default" : color.ToString();

    // Null for Default, which is the theme's own page color.
    public static Color? Paper(NoteColor color, bool dark) => (color, dark) switch
    {
        (NoteColor.Yellow, false) => Color.FromArgb(255, 0xFF, 0xF5, 0x9D),
        (NoteColor.Pink, false) => Color.FromArgb(255, 0xFF, 0xC1, 0xDA),
        (NoteColor.Green, false) => Color.FromArgb(255, 0xBB, 0xF2, 0xB0),
        (NoteColor.Blue, false) => Color.FromArgb(255, 0xA9, 0xDC, 0xFF),
        (NoteColor.Orange, false) => Color.FromArgb(255, 0xFF, 0xD3, 0xA0),
        (NoteColor.Purple, false) => Color.FromArgb(255, 0xDA, 0xC4, 0xFF),
        (NoteColor.Yellow, true) => Color.FromArgb(255, 0x6B, 0x5E, 0x12),
        (NoteColor.Pink, true) => Color.FromArgb(255, 0x6E, 0x2A, 0x48),
        (NoteColor.Green, true) => Color.FromArgb(255, 0x24, 0x5C, 0x35),
        (NoteColor.Blue, true) => Color.FromArgb(255, 0x1F, 0x4E, 0x79),
        (NoteColor.Orange, true) => Color.FromArgb(255, 0x7A, 0x45, 0x12),
        (NoteColor.Purple, true) => Color.FromArgb(255, 0x4B, 0x2E, 0x6E),
        _ => null
    };
}

// The menu of paper colors behind the editor's color button.
public static class PaperMenu
{
    public static MenuFlyout Create(Func<NoteColor> current, Func<bool> isDark, Action<NoteColor> pick)
    {
        MenuFlyout menu = new();
        List<(NoteColor Color, RadioMenuFlyoutItem Item)> items = [];
        foreach (NoteColor color in NoteColors.All)
        {
            RadioMenuFlyoutItem item = new() { Text = NoteColors.Label(color), GroupName = "Paper" };
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(item, $"Paper{color}");
            item.Click += (_, _) => pick(color);
            items.Add((color, item));
            menu.Items.Add(item);
        }
        // Swatches follow the theme and the page's color, so they are set each time the menu opens.
        menu.Opening += (_, _) =>
        {
            foreach ((NoteColor color, RadioMenuFlyoutItem item) in items)
            {
                item.Icon = Swatch(NoteColors.Paper(color, isDark()));
                item.IsChecked = color == current();
            }
        };
        return menu;
    }

    private static PathIcon Swatch(Color? paper) => new()
    {
        Data = new EllipseGeometry { Center = new Point(8, 8), RadiusX = 7, RadiusY = 7 },
        Foreground = paper is Color fill ? new SolidColorBrush(fill) : (Brush)Application.Current.Resources["TextFillColorDisabledBrush"]
    };
}
