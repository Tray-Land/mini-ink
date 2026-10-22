using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;
using Windows.UI;
using Windows.UI.Input.Inking;

namespace MiniInk;

// Fills an ink toolbar with the user's pens and keeps the two in step. A pen picked up, recolored or
// resized here is remembered for every toolbar and the next session; pens added, removed or changed
// elsewhere show here. The eraser and ruler are the toolbar's own, declared where it is.
public sealed class PenToolbar
{
    // The palettes and widths the toolbar's own pen, pencil and highlighter buttons have, which each pen of a kind matches.
    private static readonly uint[] InkPalette =
    [
        0x000000, 0xFFFFFF, 0xD1D3D4, 0xA7A9AC, 0x808285, 0x58595B, 0xB31564, 0xE61B1B, 0xFF5500, 0xFFAA00,
        0xFFCE00, 0xFFE600, 0xA2E61B, 0x26E600, 0x008055, 0x00AACC, 0x004DE6, 0x3D00B8, 0x6600CC, 0x600080,
        0xF7D7C4, 0xBB9167, 0x8E562E, 0x613D30, 0xFF80FF, 0xFFC680, 0xFFFF80, 0x80FF9E, 0x80D6FF, 0xBCB3FF
    ];
    private static readonly uint[] HighlighterPalette = [0xFFE600, 0x26E600, 0x44C8F5, 0xEC008C, 0xFF5500, 0x6600CC];

    private readonly InkToolbar _toolbar;
    private readonly InkCanvas _ink;
    private readonly List<InkToolbarPenButton> _buttons = [];
    // The part of a pen button's icon that shows the ink color.
    private readonly Dictionary<InkToolbarPenButton, TextBlock> _fills = [];
    // Pens that have loaded and been set up as they were left. The toolbar reports its own starting color and
    // size as it sets itself up, and those are not choices, so nothing is remembered from a pen before then.
    private readonly HashSet<InkToolbarPenButton> _ready = [];
    private readonly PenStore _store;
    private readonly Func<ElementTheme> _pageTheme;
    private PenSet? _shown;
    private bool _applying;

    // Raised once the toolbar has been filled again, which can reset how the canvas takes input.
    public event EventHandler? Filled;

    public PenToolbar(InkToolbar toolbar, InkCanvas ink, PenStore store, Func<ElementTheme> pageTheme)
    {
        _toolbar = toolbar;
        _ink = ink;
        _store = store;
        _pageTheme = pageTheme;
        toolbar.TargetInkCanvas = ink;
        // The canvas draws with exactly what the toolbar shows.
        toolbar.InkDrawingAttributesChanged += (_, _) => AttributesChanged();
        toolbar.ActiveToolChanged += (_, _) => ToolChanged();
        // Pens are read when the toolbar shows, and followed while it does.
        toolbar.Loaded += (_, _) =>
        {
            _store.Changed -= Store_Changed;
            _store.Changed += Store_Changed;
            Show(_store.Current);
        };
        toolbar.Unloaded += (_, _) =>
        {
            Detach();
            _ready.Clear();
        };
    }

    // Ink that shows on the page: white when it is dark, black when it is light.
    public static Color InkFor(ElementTheme theme) => theme == ElementTheme.Dark ? Colors.White : Colors.Black;

    public void Detach() => _store.Changed -= Store_Changed;

    // The page changed between light and dark; pens that have not been given a color follow it.
    public void RefreshInk()
    {
        if (_shown is null)
            return;
        Applying(() => ForEachPen((button, pen) => Apply(button, pen)));
        Sync();
    }

    private void Store_Changed(object? sender, PenChange change)
    {
        if (!ReferenceEquals(change.Source, this))
            Show(change.Pens);
    }

    private void Show(PenSet pens)
    {
        bool rebuild = _shown is null || !_shown.SameTools(pens);
        _shown = pens;
        if (rebuild)
            Fill(pens);
        else
            RefreshInk();
    }

    private void Fill(PenSet pens)
    {
        Applying(() =>
        {
            foreach (InkToolbarPenButton old in _buttons)
                _toolbar.Children.Remove(old);
            _buttons.Clear();
            _fills.Clear();
            _ready.Clear();
            InkToolbarToolButton? active = null;
            foreach (PenSpec pen in pens.Pens)
            {
                InkToolbarPenButton button = Create(pen.Kind);
                string label = pens.Label(pen);
                button.Tag = pen.Id;
                AutomationProperties.SetName(button, label);
                AutomationProperties.SetAutomationId(button, $"Pen{_buttons.Count}");
                ToolTipService.SetToolTip(button, label);
                button.ContextFlyout = MoveMenu(pen.Id);
                button.Loaded += PenButton_Loaded;
                Apply(button, pen);
                // Pens come first, ahead of the toolbar's own eraser and ruler.
                _toolbar.Children.Insert(_buttons.Count, button);
                _buttons.Add(button);
                if (pen.Id == pens.Active.Id)
                    active = button;
            }
            if (active is not null)
                _toolbar.ActiveTool = active;
        });
        Sync();
        Filled?.Invoke(this, EventArgs.Empty);
    }

    // Right-click, or press and hold, on a pen to move it along the toolbar.
    private MenuFlyout MoveMenu(string id)
    {
        MenuFlyoutItem left = new() { Text = "Move left", Icon = new FontIcon { Glyph = "" } };
        MenuFlyoutItem right = new() { Text = "Move right", Icon = new FontIcon { Glyph = "" } };
        AutomationProperties.SetAutomationId(left, "MovePenLeft");
        AutomationProperties.SetAutomationId(right, "MovePenRight");
        // Edited without a source, so this toolbar is filled again in the new order along with the others.
        left.Click += (_, _) => _store.Edit(pens => pens.Move(id, pens.IndexOf(id) - 1));
        right.Click += (_, _) => _store.Edit(pens => pens.Move(id, pens.IndexOf(id) + 1));
        MenuFlyout menu = new();
        menu.Items.Add(left);
        menu.Items.Add(right);
        menu.Opening += (_, _) =>
        {
            int index = _store.Current.IndexOf(id);
            left.IsEnabled = index > 0;
            right.IsEnabled = index >= 0 && index < _store.Current.Pens.Length - 1;
        };
        return menu;
    }

    // Every pen is a custom pen button, whatever its kind: the toolbar always lays out its own pen, pencil and
    // highlighter buttons in that order, which would leave a pen nowhere else to go.
    private InkToolbarPenButton Create(PenKind kind)
    {
        (double min, double max, double size) = Widths(kind);
        // The same two glyphs the toolbar draws its own pen buttons with: an outline over a fill that takes the ink color.
        (string outline, string filled) = kind switch
        {
            PenKind.Ballpoint => ("", ""),
            PenKind.Pencil => ("", ""),
            _ => ("", "")
        };
        TextBlock fill = Glyph(filled);
        Grid icon = new() { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        icon.Children.Add(fill);
        icon.Children.Add(Glyph(outline));
        InkToolbarCustomPenButton button = new()
        {
            CustomPen = new PlainPen(),
            Palette = (kind == PenKind.Highlighter ? HighlighterPalette : InkPalette)
                .Select(rgb => (Brush)new SolidColorBrush(ToColor(0xFF000000 | rgb))).ToList(),
            MinStrokeWidth = min,
            MaxStrokeWidth = max,
            SelectedStrokeWidth = size,
            SelectedBrushIndex = 0,
            ConfigurationContent = new InkToolbarPenConfigurationControl(),
            Content = icon
        };
        _fills[button] = fill;
        return button;
    }

    // Drawn as the toolbar draws its own: the outline takes the button's foreground, so it follows the checked state.
    private static TextBlock Glyph(string glyph) => new()
    {
        Text = glyph,
        FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"),
        FontSize = 16,
        LineHeight = 16,
        LineStackingStrategy = LineStackingStrategy.BlockLineHeight,
        TextLineBounds = TextLineBounds.Tight,
        TextAlignment = TextAlignment.Center,
        VerticalAlignment = VerticalAlignment.Center
    };

    private static (double Min, double Max, double Default) Widths(PenKind kind) => kind switch
    {
        PenKind.Ballpoint => (1, 24, 2),
        PenKind.Pencil => (1, 24, 4),
        _ => (12, 64, 16)
    };

    // A pen is set up as it was left once it has loaded, since the toolbar reports its own starting color and size as
    // it does; only after that are its changes the user's.
    private void PenButton_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is InkToolbarPenButton { Tag: string id } button && _store.Current.Find(id) is PenSpec pen)
        {
            Applying(() => Apply(button, pen));
            _ready.Add(button);
            Sync();
        }
    }

    // A pen shows the color and size it was left at; without a color, a pen or pencil takes the page's ink.
    private void Apply(InkToolbarPenButton button, PenSpec pen)
    {
        Color? color = pen.Color is uint argb ? ToColor(argb) : PenKinds.FollowsPage(pen.Kind) ? InkFor(_pageTheme()) : null;
        if (color is Color ink)
        {
            int index = button.Palette.ToList().FindIndex(brush => brush is SolidColorBrush solid && solid.Color == ink);
            if (index < 0)
            {
                button.Palette = [new SolidColorBrush(ink), .. button.Palette];
                index = 0;
            }
            button.SelectedBrushIndex = index;
        }
        if (pen.Size is double size)
            button.SelectedStrokeWidth = Math.Clamp(size, button.MinStrokeWidth, button.MaxStrokeWidth);
        ShowInk(button);
    }

    // Colors the fill of a pen's icon with its ink.
    private void ShowInk(InkToolbarPenButton button)
    {
        if (_fills.TryGetValue(button, out TextBlock? fill))
            fill.Foreground = button.SelectedBrush;
    }

    private void AttributesChanged()
    {
        Sync();
        if (_applying || ActivePen() is not { } active || !_ready.Contains(active.Button) || active.Button.SelectedBrush is not SolidColorBrush brush)
            return;
        (InkToolbarPenButton button, PenSpec pen) = active;
        ShowInk(button);
        // Picking the page's own ink is not a choice, so the pen keeps following the page.
        uint? color = PenKinds.FollowsPage(pen.Kind) && brush.Color == InkFor(_pageTheme()) ? null : ToArgb(brush.Color);
        _store.Edit(pens => pens.Replace(pen with { Color = color, Size = button.SelectedStrokeWidth }), this);
    }

    private void ToolChanged()
    {
        Sync();
        if (!_applying && ActivePen() is { } active && _ready.Contains(active.Button))
            _store.Edit(pens => pens.Activate(active.Pen.Id), this);
    }

    // The pen in hand, if the eraser or ruler is not.
    private (InkToolbarPenButton Button, PenSpec Pen)? ActivePen() =>
        _toolbar.ActiveTool is InkToolbarPenButton { Tag: string id } button && _store.Current.Find(id) is PenSpec pen ? (button, pen) : null;

    private void ForEachPen(Action<InkToolbarPenButton, PenSpec> action)
    {
        foreach (InkToolbarPenButton button in _buttons)
            if (button.Tag is string id && _store.Current.Find(id) is PenSpec pen)
                action(button, pen);
    }

    // The toolbar's own changes while it is being filled are not choices made by the user.
    private void Applying(Action action)
    {
        bool before = _applying;
        _applying = true;
        try
        {
            action();
        }
        finally
        {
            _applying = before;
        }
    }

    // The canvas draws with what the toolbar shows, and a pencil or highlighter with what makes it one as well.
    private void Sync()
    {
        if (_toolbar.InkDrawingAttributes is InkDrawingAttributes shown)
            _ink.InkPresenter.UpdateDefaultDrawingAttributes(ActivePen() is { } active ? Drawing(active.Pen.Kind, shown) : shown);
    }

    // The toolbar gives a custom pen the color and size picked, as plain ink; it never asks the pen for more.
    private static InkDrawingAttributes Drawing(PenKind kind, InkDrawingAttributes shown)
    {
        InkDrawingAttributes attributes = kind == PenKind.Pencil ? InkDrawingAttributes.CreateForPencil() : new InkDrawingAttributes();
        attributes.Color = shown.Color;
        attributes.Size = shown.Size;
        attributes.FitToCurve = true;
        attributes.IgnoreTilt = true;
        // A pencil's attributes refuse this even when it is false.
        if (kind == PenKind.Highlighter)
            attributes.DrawAsHighlighter = true;
        return attributes;
    }

    public static Color ToColor(uint argb) => Color.FromArgb((byte)(argb >> 24), (byte)(argb >> 16), (byte)(argb >> 8), (byte)argb);

    public static uint ToArgb(Color color) => (uint)color.A << 24 | (uint)color.R << 16 | (uint)color.G << 8 | color.B;

    // What a custom pen button needs to be a pen; how it draws is Drawing's.
    private sealed class PlainPen : InkToolbarCustomPen;
}
