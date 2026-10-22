using System.Numerics;
using Microsoft.UI;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;

namespace MiniInk;

// Adds, removes and puts in order the pens on every toolbar; each change applies at once and is remembered.
public sealed partial class PenSetPanel : UserControl
{
    private static readonly SolidColorBrush Marker = new(Colors.Gold);

    private PenDrag? _drag;

    public PenSetPanel()
    {
        InitializeComponent();
        foreach (PenKind kind in PenKinds.All)
        {
            MenuFlyoutItem item = new() { Text = PenKinds.Label(kind) };
            AutomationProperties.SetAutomationId(item, $"AddPen{kind}");
            item.Click += (_, _) => App.Current.Pens.Edit(pens => pens.Add(kind), this);
            AddMenu.Items.Add(item);
        }
        // Read each time it shows, since a pen can be recolored on a toolbar in another window.
        Loaded += (_, _) =>
        {
            App.Current.Pens.Changed -= Pens_Changed;
            App.Current.Pens.Changed += Pens_Changed;
            Refresh();
        };
        Unloaded += (_, _) => App.Current.Pens.Changed -= Pens_Changed;
    }

    private void Pens_Changed(object? sender, PenChange change) => Refresh();

    private void Refresh()
    {
        PenSet pens = App.Current.Pens.Current;
        // Rows built again are a drag that is over, or that someone else's change has ended.
        _drag = null;
        Rows.Children.Clear();
        for (int index = 0; index < pens.Pens.Length; index++)
        {
            PenSpec pen = pens.Pens[index];
            int place = index;
            string label = pens.Label(pen);
            // Rows slide aside to make room for the pen being dragged.
            Grid row = new() { ColumnSpacing = 12, MinHeight = 36, TranslationTransition = new Vector3Transition { Duration = TimeSpan.FromMilliseconds(150) } };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition());
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            GripHandle grip = new();
            AutomationProperties.SetName(grip, $"Reorder {label}");
            AutomationProperties.SetAutomationId(grip, $"MovePen{index}");
            ToolTipService.SetToolTip(grip, "Drag to reorder");
            grip.PointerPressed += (_, e) => BeginDrag(grip, row, pen.Id, place, e);
            grip.PointerMoved += Grip_PointerMoved;
            grip.PointerReleased += Grip_PointerReleased;
            grip.PointerCaptureLost += (_, _) => EndDrag(commit: false);
            TextBlock swatch = new() { Text = "●", FontSize = 18, VerticalAlignment = VerticalAlignment.Center };
            if (Ink(pen) is Brush ink)
                swatch.Foreground = ink;
            TextBlock name = new() { Text = label, VerticalAlignment = VerticalAlignment.Center };
            Button remove = new()
            {
                Content = new FontIcon { Glyph = "", FontSize = 14 },
                Style = (Style)Resources["RemovePenStyle"],
                IsEnabled = pens.CanRemove
            };
            AutomationProperties.SetName(remove, $"Remove {label}");
            AutomationProperties.SetAutomationId(remove, $"RemovePen{index}");
            ToolTipService.SetToolTip(remove, $"Remove {label}");
            remove.Click += (_, _) => App.Current.Pens.Edit(set => set.Remove(pen.Id), this);
            Grid.SetColumn(swatch, 1);
            Grid.SetColumn(name, 2);
            Grid.SetColumn(remove, 3);
            row.Children.Add(grip);
            row.Children.Add(swatch);
            row.Children.Add(name);
            row.Children.Add(remove);
            Rows.Children.Add(row);
        }
        AddButton.IsEnabled = pens.CanAdd;
        Limit.Text = pens.CanAdd
            ? "Each toolbar remembers the color and size a pen was left at."
            : $"A toolbar holds up to {PenSet.MaxPens} pens. Remove one to add another.";
    }

    // The color the pen was left in. Null for a pen or pencil with none, which writes in the page's own ink,
    // so its swatch keeps the text color of the page it sits on.
    private static Brush? Ink(PenSpec pen) => pen.Color is uint argb
        ? new SolidColorBrush(Windows.UI.Color.FromArgb((byte)(argb >> 24), (byte)(argb >> 16), (byte)(argb >> 8), (byte)argb))
        : PenKinds.FollowsPage(pen.Kind) ? null : Marker;

    private void Reset_Click(object sender, RoutedEventArgs e) => App.Current.Pens.Reset(this);

    // Dragging a pen by its handle follows the pointer, and the rows it passes slide out of its way. Nothing is
    // changed until it is let go, and then only if it has moved to another place.
    private void BeginDrag(GripHandle grip, Grid row, string id, int place, PointerRoutedEventArgs e)
    {
        if (!e.GetCurrentPoint(grip).Properties.IsLeftButtonPressed || !grip.CapturePointer(e.Pointer))
            return;
        _drag = new PenDrag(row, id, place, e.GetCurrentPoint(Rows).Position.Y, row.ActualHeight + Rows.Spacing);
        // It keeps up with the pointer rather than easing after it, and has nothing to say while it does.
        ToolTipService.SetToolTip(grip, null);
        row.TranslationTransition = null;
        row.Style = (Style)Resources["DraggedPenStyle"];
        Canvas.SetZIndex(row, 1);
        e.Handled = true;
    }

    private void Grip_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (_drag is not { } drag)
            return;
        int last = Rows.Children.Count - 1;
        double offset = Math.Clamp(e.GetCurrentPoint(Rows).Position.Y - drag.Origin, -drag.From * drag.Step, (last - drag.From) * drag.Step);
        drag.Row.Translation = new Vector3(0, (float)offset, 0);
        int to = Math.Clamp((int)Math.Round(drag.From + offset / drag.Step), 0, last);
        if (to == drag.To)
            return;
        drag.To = to;
        for (int place = 0; place <= last; place++)
        {
            if (place == drag.From)
                continue;
            // The rows between where it started and where it would land each move over by one.
            double shift = drag.From < to && place > drag.From && place <= to ? -drag.Step
                : drag.From > to && place < drag.From && place >= to ? drag.Step : 0;
            Rows.Children[place].Translation = new Vector3(0, (float)shift, 0);
        }
        e.Handled = true;
    }

    private void Grip_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        EndDrag(commit: true);
        ((UIElement)sender).ReleasePointerCapture(e.Pointer);
        e.Handled = true;
    }

    private void EndDrag(bool commit)
    {
        if (_drag is not { } drag)
            return;
        _drag = null;
        // The rows are built again either way: in the new order, or back where they were.
        if (commit && drag.To != drag.From)
            App.Current.Pens.Edit(set => set.Move(drag.Id, drag.To), this);
        else
            Refresh();
    }

    // The pen being dragged: its row, where it started, where the pointer was then, how far apart rows are,
    // and where it would land if it were let go now.
    private sealed class PenDrag(Grid row, string id, int from, double origin, double step)
    {
        public Grid Row { get; } = row;
        public string Id { get; } = id;
        public int From { get; } = from;
        public double Origin { get; } = origin;
        public double Step { get; } = step;
        public int To { get; set; } = from;
    }

    // The grip a pen is dragged by. Its whole area takes the pointer, and touch drags the pen rather than the page.
    private sealed class GripHandle : Grid
    {
        public GripHandle()
        {
            Width = 24;
            Background = new SolidColorBrush(Colors.Transparent);
            ManipulationMode = ManipulationModes.None;
            ProtectedCursor = InputSystemCursor.Create(InputSystemCursorShape.SizeAll);
            Children.Add(new FontIcon { Glyph = "", FontSize = 16, Opacity = 0.7, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center });
        }
    }
}
