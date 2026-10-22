using System.Diagnostics;
using System.Globalization;
using Microsoft.UI;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.Foundation;
using Windows.Storage.Streams;
using Windows.System;
using Windows.UI.Core;
using Windows.UI.Input.Inking;

namespace MiniInk;

public sealed partial class EditorPage : Page
{
    private readonly Note _note;
    private readonly List<TextNote> _texts;
    private readonly SemaphoreSlim _operations = new(1, 1);
    private readonly DispatcherTimer _autosave = new() { Interval = TimeSpan.FromSeconds(1.2) };
    private HashSet<uint> _selectedInk = [];
    private TextNote? _selectedText;
    private TextNote? _editing;
    private long _revision;
    private long _savedRevision;
    // Drawn again only once the ink has changed.
    private string? _thumbnail;
    private bool _loaded;
    private bool _loadFailed;
    private bool _updatingText;
    private bool _detached;
    private bool _closing;
    private bool _inking;
    private bool _suspended;
    private bool _isDraft;
    private string _titleText;
    private readonly string _draftTitle;
    private readonly TaskCompletionSource _loadComplete = new();
    private bool _pointerDown;
    private bool _dragging;
    private Point _pressed;
    private Point _last;
    private uint _pointerId;
    private InputTool _pointerTool;
    private bool _reselected;
    private InputTool _mouseTool;
    private InputTool _touchTool;
    private PenToolbar? _pens;
    private Flyout? _inputTools;
    private MenuFlyout? _paperMenu;

    public Guid NoteId => _note.Id;
    // A blank page that has not been written to disk yet.
    public bool IsDraft => _isDraft;
    public NoteTemplate NoteTemplate => _note.Template;
    public TemplateOptions? TemplateOptions => _note.Options;
    public NoteColor Paper => _note.Paper;
    // Nothing written yet; a template's lines and suggested title don't count.
    public bool IsBlankPage => _isDraft && NoteTitle.Text.Trim() == _draftTitle &&
        _texts.All(t => string.IsNullOrWhiteSpace(t.Text)) && Ink.InkPresenter.StrokeContainer.GetStrokes().Count == 0;
    public string SavedTitle => string.IsNullOrWhiteSpace(NoteTitle.Text) ? Note.Untitled : NoteTitle.Text.Trim();
    // Raised when a blank page is first written to disk.
    public event EventHandler? Written;

    public EditorPage(Note note, bool draft = false, bool sticky = false)
    {
        _note = note;
        _isDraft = draft;
        _texts = note.Texts.Select(t => new TextNote { Id = t.Id, Text = t.Text, X = t.X, Y = t.Y }).ToList();
        InitializeComponent();
        _titleText = note.Title == Note.Untitled ? "" : note.Title;
        _draftTitle = _titleText.Trim();
        NoteTitle.Text = _titleText;
        ApplyPaper();
        ApplyInputTools();
        // A sticky note is just the page; its window carries a smaller set of pen tools.
        if (sticky)
        {
            Layout.Padding = new Thickness(0);
            Layout.RowSpacing = 0;
            Header.Visibility = Commands.Visibility = Visibility.Collapsed;
        }
        else
        {
            UsePenTools(PenTools);
            NewPageButton.Flyout = TemplateMenu.Create();
            _paperMenu = PaperMenu.Create(() => _note.Paper, SetPaper);
        }
        Ink.InkPresenter.StrokesCollected += (_, _) => InkChanged();
        Ink.InkPresenter.StrokesErased += (_, _) => InkChanged();
        Ink.InkPresenter.StrokeInput.StrokeStarted += (_, _) => _inking = true;
        Ink.InkPresenter.StrokeInput.StrokeEnded += (_, _) => _inking = false;
        Ink.InkPresenter.StrokeInput.StrokeCanceled += (_, _) => _inking = false;
        Surface.AddHandler(PointerPressedEvent, new PointerEventHandler(Surface_PointerPressed), true);
        Surface.AddHandler(PointerMovedEvent, new PointerEventHandler(Surface_PointerMoved), true);
        Surface.AddHandler(PointerReleasedEvent, new PointerEventHandler(Surface_PointerReleased), true);
        Surface.PointerCanceled += Surface_PointerCanceled;
        Surface.PointerCaptureLost += Surface_PointerCanceled;
        Surface.SizeChanged += (_, _) => RenderTemplate();
        TextEditor.PointerEntered += (_, e) =>
        {
            if (e.Pointer.PointerDeviceType == PointerDeviceType.Pen)
                FinishEditing();
        };
        KeyDown += Editor_KeyDown;
        _autosave.Tick += Autosave_Tick;
        Loaded += Editor_Loaded;
        // A tool chosen in any window applies here while the page is shown.
        Loaded += (_, _) =>
        {
            App.Current.InputToolsChanged -= InputTools_Changed;
            App.Current.InputToolsChanged += InputTools_Changed;
            ApplyInputTools();
        };
        Unloaded += (_, _) => App.Current.InputToolsChanged -= InputTools_Changed;
        ActualThemeChanged += (_, _) =>
        {
            ApplyPaper();
            _pens?.RefreshInk();
            RenderTemplate();
        };
    }

    private void SetPaper(NoteColor color)
    {
        if (_note.Paper == color)
            return;
        _note.Paper = color;
        ApplyPaper();
        _pens?.RefreshInk();
        RenderTemplate();
        Changed();
    }

    // A sticky color is a bright page whatever the theme, so the page, with its text, goes light over it.
    private ElementTheme PageTheme => _note.Paper == NoteColor.Default ? ActualTheme : ElementTheme.Light;

    private void ApplyPaper()
    {
        if (NoteColors.Paper(_note.Paper) is Windows.UI.Color color)
        {
            PaperColor.Background = new SolidColorBrush(color);
            PaperColor.Visibility = Visibility.Visible;
            Surface.RequestedTheme = ElementTheme.Light;
        }
        else
        {
            PaperColor.Visibility = Visibility.Collapsed;
            Surface.RequestedTheme = ElementTheme.Default;
        }
    }

    // The toolbar holds the user's pens, as they were left, and draws on this page's canvas.
    public void UsePenTools(InkToolbar tools)
    {
        _pens?.Detach();
        _pens = new PenToolbar(tools, Ink, App.Current.Pens, () => PageTheme);
        // Filling the toolbar can reset how the canvas takes input.
        _pens.Filled += (_, _) => ApplyInputTools();
        tools.Loaded += (_, _) => ApplyInputTools();
    }

    private void InputTools_Changed(object? sender, EventArgs e) => ApplyInputTools();

    // The pen always draws; mouse and touch draw only when Draw is their tool.
    private void ApplyInputTools()
    {
        _mouseTool = App.Current.MouseTool;
        _touchTool = App.Current.TouchTool;
        CoreInputDeviceTypes drawing = CoreInputDeviceTypes.Pen;
        if (_mouseTool == InputTool.Draw)
            drawing |= CoreInputDeviceTypes.Mouse;
        if (_touchTool == InputTool.Draw)
            drawing |= CoreInputDeviceTypes.Touch;
        Ink.InkPresenter.InputDeviceTypes = drawing;
    }

    private InputTool ToolFor(PointerDeviceType device) => device switch
    {
        PointerDeviceType.Mouse => _mouseTool,
        PointerDeviceType.Touch => _touchTool,
        _ => InputTool.Draw
    };

    private async void Editor_Loaded(object sender, RoutedEventArgs e)
    {
        if (_loaded || _loadFailed)
            return;
        try
        {
            if (_note.InkBase64.Length > 0)
            {
                using InMemoryRandomAccessStream stream = new();
                using (DataWriter writer = new(stream.GetOutputStreamAt(0)))
                {
                    writer.WriteBytes(Convert.FromBase64String(_note.InkBase64));
                    await writer.StoreAsync();
                    await writer.FlushAsync();
                    writer.DetachStream();
                }
                stream.Seek(0);
                await Ink.InkPresenter.StrokeContainer.LoadAsync(stream.GetInputStreamAt(0));
            }
            RenderText();
            FitSurface();
            _loaded = true;
        }
        catch (Exception ex)
        {
            _loadFailed = true;
            Ink.InkPresenter.IsInputEnabled = false;
            Surface.IsHitTestVisible = false;
            NoteTitle.IsEnabled = false;
            ShowError($"This note could not be opened. Its saved file has not been changed: {ex.Message}", ex);
        }
        finally
        {
            _loadComplete.TrySetResult();
        }
    }

    private void Changed(bool inkChanged = false)
    {
        if (!_loaded || _detached)
            return;
        _revision++;
        if (inkChanged)
            _thumbnail = null;
        _autosave.Stop();
        _autosave.Start();
    }

    private void InkChanged()
    {
        ClearSelection();
        Changed(inkChanged: true);
    }

    private void CanvasScroll_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (_loaded)
            FitSurface();
    }

    // The page fills its window, and grows past it only for content placed further out.
    private void FitSurface()
    {
        FitHours();
        Size content = ContentSize();
        Surface.Width = Math.Max(content.Width, CanvasScroll.ActualWidth);
        Surface.Height = Math.Max(content.Height, CanvasScroll.ActualHeight);
    }

    // An hour planner's rows fill the window until something is written; then they stay where they are.
    private void FitHours()
    {
        if (_note is not { Template: NoteTemplate.Day, Options: TemplateOptions options } || !IsBlankPage || CanvasScroll.ActualHeight <= 0)
            return;
        TemplateOptions fitted = NoteTemplates.FitHours(options, CanvasScroll.ActualHeight);
        if (fitted == options)
            return;
        _note.Options = fitted;
        _note.CanvasHeight = NoteTemplates.DayHeight(fitted);
        RenderTemplate();
    }

    private Size ContentSize()
    {
        Rect inkBounds = Ink.InkPresenter.StrokeContainer.BoundingRect;
        double width = Math.Max(_note.CanvasWidth, inkBounds.IsEmpty ? 0 : inkBounds.Right + 16);
        double height = Math.Max(_note.CanvasHeight, inkBounds.IsEmpty ? 0 : inkBounds.Bottom + 16);
        foreach (TextNote text in _texts)
        {
            width = Math.Max(width, text.X + 220);
            height = Math.Max(height, text.Y + TextHeight(text));
        }
        return new Size(width, height);
    }

    private void Title_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (NoteTitle.Text == _titleText)
            return;
        _titleText = NoteTitle.Text;
        Changed();
    }
    private void TextEditor_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_updatingText || _editing is null || _editing.Text == TextEditor.Text)
            return;
        _editing.Text = TextEditor.Text;
        RenderText();
        Changed();
    }
    private void TextEditor_LostFocus(object sender, RoutedEventArgs e) => FinishEditing();

    private void FinishEditing()
    {
        if (_editing is null)
            return;
        TextNote text = _editing;
        _editing = null;
        TextEditor.Visibility = Visibility.Collapsed;
        if (string.IsNullOrWhiteSpace(text.Text))
        {
            _texts.Remove(text);
            if (_selectedText == text)
                ClearSelection();
            Changed();
        }
        RenderText();
    }

    private void EditText(TextNote text)
    {
        FinishEditing();
        _editing = _selectedText = text;
        _selectedInk.Clear();
        _updatingText = true;
        TextEditor.Text = text.Text;
        _updatingText = false;
        Canvas.SetLeft(TextEditor, text.X);
        Canvas.SetTop(TextEditor, text.Y);
        TextEditor.Visibility = Visibility.Visible;
        TextEditor.Focus(FocusState.Programmatic);
        TextEditor.SelectionStart = TextEditor.Text.Length;
        RenderText();
        UpdateSelection();
    }

    private void AddText(Point point)
    {
        FinishEditing();
        TextNote text = new() { X = Math.Clamp(point.X, 0, Math.Max(0, Surface.Width - 220)),
            Y = Math.Clamp(point.Y, 0, Math.Max(0, Surface.Height - 40)) };
        _texts.Add(text);
        Changed();
        EditText(text);
    }

    private void RenderText()
    {
        TextLayer.Children.Clear();
        foreach (TextNote text in _texts)
        {
            TextBlock block = new()
            {
                Text = text.Text,
                FontSize = 18,
                Width = 220,
                TextWrapping = TextWrapping.Wrap,
                Visibility = text == _editing ? Visibility.Collapsed : Visibility.Visible
            };
            AutomationProperties.SetName(block, text.Text);
            Canvas.SetLeft(block, text.X);
            Canvas.SetTop(block, text.Y);
            TextLayer.Children.Add(block);
        }
    }

    // The template's printed lines, in the page's ink color, redrawn as the page grows.
    private void RenderTemplate()
    {
        TemplateLayer.Children.Clear();
        if (_note.Template == NoteTemplate.None)
            return;
        Windows.UI.Color ink = PenToolbar.InkFor(PageTheme);
        SolidColorBrush faint = new(ColorHelper.FromArgb(0x24, ink.R, ink.G, ink.B));
        SolidColorBrush strong = new(ColorHelper.FromArgb(0x50, ink.R, ink.G, ink.B));
        SolidColorBrush label = new(ColorHelper.FromArgb(0x99, ink.R, ink.G, ink.B));
        foreach (TemplateMark mark in NoteTemplates.Layout(_note.Template, Surface.Width, Surface.Height, CultureInfo.CurrentCulture, _note.Options))
        {
            switch (mark)
            {
                case TemplateLine line:
                    Line rule = new()
                    {
                        X1 = line.X1, Y1 = line.Y1, X2 = line.X2, Y2 = line.Y2,
                        Stroke = line.Strong ? strong : faint,
                        StrokeThickness = line.Thickness
                    };
                    // A dash of no length with round caps is a dot.
                    if (line.DotSpacing > 0)
                    {
                        rule.StrokeDashArray = new DoubleCollection { 0, line.DotSpacing / line.Thickness };
                        rule.StrokeDashCap = rule.StrokeStartLineCap = rule.StrokeEndLineCap = PenLineCap.Round;
                    }
                    TemplateLayer.Children.Add(rule);
                    break;
                case TemplateBox box:
                    Place(new Rectangle
                    {
                        Width = box.Size, Height = box.Size, RadiusX = 3, RadiusY = 3, Stroke = strong, StrokeThickness = 1.5
                    }, box.X, box.Y);
                    break;
                case TemplateLabel text:
                    Place(new TextBlock
                    {
                        Text = text.Text, FontSize = text.Size, Foreground = label,
                        FontWeight = text.Bold ? Microsoft.UI.Text.FontWeights.SemiBold : Microsoft.UI.Text.FontWeights.Normal
                    }, text.X, text.Y);
                    break;
            }
        }

        void Place(UIElement element, double x, double y)
        {
            Canvas.SetLeft(element, x);
            Canvas.SetTop(element, y);
            TemplateLayer.Children.Add(element);
        }
    }

    private TextNote? HitText(Point point) => _texts.LastOrDefault(t =>
        new Rect(t.X, t.Y, 220, TextHeight(t)).Contains(point));

    private static double TextHeight(TextNote text)
    {
        TextBlock measure = new() { Text = text.Text, FontSize = 18, Width = 220, TextWrapping = TextWrapping.Wrap };
        measure.Measure(new Size(220, double.PositiveInfinity));
        return Math.Max(44, measure.DesiredSize.Height);
    }

    private void Surface_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (!_loaded || _loadFailed || _pointerDown || _closing)
            return;
        PointerDeviceType device = e.Pointer.PointerDeviceType;
        var current = e.GetCurrentPoint(Surface);
        if (device == PointerDeviceType.Mouse && !current.Properties.IsLeftButtonPressed)
            return;
        Point point = current.Position;
        // Clicks and taps inside the open text box are for the text.
        if (device != PointerDeviceType.Pen && _editing is not null &&
            new Rect(_editing.X, _editing.Y, TextEditor.ActualWidth, TextEditor.ActualHeight).Contains(point))
            return;
        FinishEditing();
        InputTool tool = ToolFor(device);
        if (tool == InputTool.Draw)
        {
            ClearSelection();
            return;
        }
        _pointerDown = true;
        _pointerId = e.Pointer.PointerId;
        _pointerTool = tool;
        _pressed = _last = point;
        _dragging = false;
        TextNote? text = HitText(point);
        _reselected = text is not null && text == _selectedText;
        if (text is not null)
        {
            ClearSelection();
            _selectedText = text;
            UpdateSelection();
        }
        else if (!SelectedBounds().Contains(point))
        {
            ClearSelection();
        }
        Surface.CapturePointer(e.Pointer);
        e.Handled = true;
    }

    private void Surface_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_pointerDown || e.Pointer.PointerId != _pointerId)
            return;
        Point point = e.GetCurrentPoint(Surface).Position;
        if (!_dragging && Math.Sqrt(Math.Pow(point.X - _pressed.X, 2) + Math.Pow(point.Y - _pressed.Y, 2)) > 6)
            _dragging = true;
        if (_dragging && (_selectedText is not null || _selectedInk.Count > 0))
            MoveSelection(point.X - _last.X, point.Y - _last.Y);
        _last = point;
        e.Handled = true;
    }

    private void Surface_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (!_pointerDown || e.Pointer.PointerId != _pointerId)
            return;
        bool dragged = _dragging;
        Point point = e.GetCurrentPoint(Surface).Position;
        _pointerDown = false;
        Surface.ReleasePointerCapture(e.Pointer);
        e.Handled = true;
        if (dragged)
            return;
        if (_pointerTool == InputTool.Text)
        {
            if (_selectedText is not null)
                EditText(_selectedText);
            else
                AddText(point);
            return;
        }
        // Selecting: a second click or tap on selected text edits it, and ink is picked up by group.
        if (_selectedText is not null)
        {
            if (_reselected)
                EditText(_selectedText);
            return;
        }
        if (InkGroupAt(point) is { Count: > 0 } group)
        {
            ClearSelection();
            _selectedInk = group;
            UpdateSelection();
        }
    }

    private void Surface_PointerCanceled(object sender, PointerRoutedEventArgs e)
    {
        if (e.Pointer.PointerId == _pointerId)
            _pointerDown = false;
    }

    private static Rect Inflate(Rect rect, double padding) => new(
        rect.X - padding, rect.Y - padding, rect.Width + padding * 2, rect.Height + padding * 2);

    // Ink is picked up a word or drawing at a time: the stroke at the point, and every stroke
    // that touches it, or comes within a few pixels of one that does.
    private HashSet<uint> InkGroupAt(Point point)
    {
        (uint Id, Rect Bounds)[] strokes = Ink.InkPresenter.StrokeContainer.GetStrokes().Select(s => (s.Id, s.BoundingRect)).ToArray();
        (uint Id, Rect Bounds)[] hits = strokes.Where(s => Inflate(s.Bounds, 12).Contains(point)).ToArray();
        if (hits.Length == 0)
            return [];
        (uint Id, Rect Bounds) hit = hits.MinBy(s => s.Bounds.Width * s.Bounds.Height);
        List<(uint Id, Rect Near)> rest = strokes.Where(s => s.Id != hit.Id).Select(s => (s.Id, Inflate(s.Bounds, 4))).ToList();
        HashSet<uint> group = [hit.Id];
        Queue<Rect> reached = new([Inflate(hit.Bounds, 4)]);
        while (reached.TryDequeue(out Rect near))
        {
            foreach ((uint Id, Rect Near) stroke in rest.Where(s => near.X <= s.Near.Right && s.Near.X <= near.Right &&
                near.Y <= s.Near.Bottom && s.Near.Y <= near.Bottom).ToArray())
            {
                group.Add(stroke.Id);
                reached.Enqueue(stroke.Near);
                rest.Remove(stroke);
            }
        }
        return group;
    }

    private Rect SelectedBounds()
    {
        if (_selectedText is not null)
            return new Rect(_selectedText.X, _selectedText.Y, 220, TextHeight(_selectedText));
        InkStroke[] strokes = Ink.InkPresenter.StrokeContainer.GetStrokes().Where(s => _selectedInk.Contains(s.Id)).ToArray();
        if (strokes.Length == 0)
            return Rect.Empty;
        Rect bounds = strokes[0].BoundingRect;
        foreach (InkStroke stroke in strokes.Skip(1))
            bounds.Union(stroke.BoundingRect);
        return bounds;
    }

    private void ClearSelection()
    {
        _selectedText = null;
        _selectedInk.Clear();
        foreach (InkStroke stroke in Ink.InkPresenter.StrokeContainer.GetStrokes())
            stroke.Selected = false;
        Selection.Visibility = Visibility.Collapsed;
    }

    private void UpdateSelection()
    {
        Rect bounds = SelectedBounds();
        Selection.Visibility = bounds.IsEmpty ? Visibility.Collapsed : Visibility.Visible;
        if (bounds.IsEmpty)
            return;
        bounds = Inflate(bounds, 4);
        Canvas.SetLeft(Selection, bounds.X);
        Canvas.SetTop(Selection, bounds.Y);
        Selection.Width = Math.Max(8, bounds.Width);
        Selection.Height = Math.Max(8, bounds.Height);
    }

    private void MoveSelection(double dx, double dy)
    {
        Rect bounds = SelectedBounds();
        if (bounds.IsEmpty)
            return;
        dx = Math.Clamp(dx, -bounds.X, Math.Max(-bounds.X, Surface.Width - bounds.Right));
        dy = Math.Clamp(dy, -bounds.Y, Math.Max(-bounds.Y, Surface.Height - bounds.Bottom));
        if (dx == 0 && dy == 0)
            return;
        if (_selectedText is not null)
        {
            _selectedText.X += dx;
            _selectedText.Y += dy;
            RenderText();
            Changed();
        }
        else
        {
            foreach (InkStroke stroke in Ink.InkPresenter.StrokeContainer.GetStrokes())
                stroke.Selected = _selectedInk.Contains(stroke.Id);
            Ink.InkPresenter.StrokeContainer.MoveSelected(new Point(dx, dy));
            Changed(inkChanged: true);
        }
        UpdateSelection();
    }

    private async void Autosave_Tick(object? sender, object e)
    {
        _autosave.Stop();
        if (_inking || _pointerDown)
        {
            _autosave.Start();
            return;
        }
        await SaveAsync();
    }

    public async Task<bool> SaveAsync()
    {
        await _loadComplete.Task;
        if (_loadFailed)
            return false;
        await _operations.WaitAsync();
        try
        {
            return await SaveCoreAsync();
        }
        catch (Exception ex)
        {
            ShowError($"Not saved. Your note stays open here; try Save again: {ex.Message}", ex);
            return false;
        }
        finally
        {
            _operations.Release();
        }
    }

    private async Task<bool> SaveCoreAsync()
    {
        while (_inking || _pointerDown)
            await Task.Delay(50);
        long revision = _revision;
        Size content = ContentSize();
        Note snapshot = new()
        {
            Id = _note.Id,
            Title = SavedTitle,
            Updated = DateTimeOffset.Now,
            Texts = _texts.Where(t => !string.IsNullOrWhiteSpace(t.Text))
                .Select(t => new TextNote { Id = t.Id, Text = t.Text, X = t.X, Y = t.Y }).ToList(),
            CanvasWidth = content.Width,
            CanvasHeight = content.Height,
            Template = _note.Template,
            Paper = _note.Paper,
            Options = _note.Options,
            StrokeCount = Ink.InkPresenter.StrokeContainer.GetStrokes().Count
        };
        // Freeze only for the short ISF snapshot.
        Ink.InkPresenter.IsInputEnabled = false;
        Surface.IsHitTestVisible = false;
        try
        {
            using InMemoryRandomAccessStream stream = new();
            using (IOutputStream output = stream.GetOutputStreamAt(0))
                await Ink.InkPresenter.StrokeContainer.SaveAsync(output);
            stream.Seek(0);
            using DataReader reader = new(stream.GetInputStreamAt(0));
            await reader.LoadAsync(checked((uint)stream.Size));
            byte[] bytes = new byte[checked((int)stream.Size)];
            reader.ReadBytes(bytes);
            snapshot.InkBase64 = Convert.ToBase64String(bytes);
        }
        finally
        {
            Ink.InkPresenter.IsInputEnabled = !_closing;
            Surface.IsHitTestVisible = !_closing;
        }
        snapshot.Thumbnail = _thumbnail ?? (snapshot.StrokeCount == 0 ? "" : await InkThumbnail.FromInkAsync(snapshot.InkBase64));
        if (_revision == revision)
            _thumbnail = snapshot.Thumbnail;
        await App.Current.Notes.SaveAsync(snapshot);
        _savedRevision = revision;
        if (_isDraft)
        {
            _isDraft = false;
            Written?.Invoke(this, EventArgs.Empty);
        }
        _note.InkBase64 = snapshot.InkBase64;
        _note.Title = snapshot.Title;
        if (_revision != revision)
        {
            _autosave.Stop();
            if (!_suspended)
                _autosave.Start();
        }
        return true;
    }

    public async Task<bool> PrepareToCloseAsync()
    {
        // An untouched blank page is never written, so empty notes don't pile up.
        if (IsBlankPage)
        {
            _autosave.Stop();
            return true;
        }
        await _loadComplete.Task;
        _closing = true;
        _autosave.Stop();
        FinishEditing();
        if (_loadFailed)
        {
            _closing = false;
            return true;
        }
        NoteTitle.IsEnabled = false;
        Surface.IsHitTestVisible = false;
        Ink.InkPresenter.IsInputEnabled = false;
        try
        {
            // Let an in-flight save finish, then write only what changed,
            // so merely viewing a note does not move it to the top of the stack.
            await _operations.WaitAsync();
            _operations.Release();
            while (_loaded && _revision != _savedRevision)
            {
                if (!await SaveAsync())
                    return false;
            }
            return true;
        }
        finally
        {
            _closing = false;
            NoteTitle.IsEnabled = true;
            Surface.IsHitTestVisible = true;
            Ink.InkPresenter.IsInputEnabled = true;
        }
    }

    public async Task SuspendAsync()
    {
        _suspended = true;
        _pointerDown = false;
        Surface.ReleasePointerCaptures();
        await PrepareToCloseAsync();
        _autosave.Stop();
    }

    public void Resume()
    {
        _suspended = false;
        if (_revision != _savedRevision && _loaded)
        {
            _autosave.Stop();
            _autosave.Start();
        }
    }

    public void Detach()
    {
        _detached = true;
        _autosave.Stop();
        _autosave.Tick -= Autosave_Tick;
        App.Current.InputToolsChanged -= InputTools_Changed;
        _pens?.Detach();
    }

    public void ShowError(string message, Exception? ex = null)
    {
        Debug.WriteLine($"MiniInk editor: {message}\n{ex}");
        Error.Message = message;
        Error.IsOpen = true;
    }

    private void AddText_Click(object sender, RoutedEventArgs e)
    {
        if (_loaded && !_loadFailed)
            AddText(new Point(24, 24));
    }
    private async void Save_Click(object sender, RoutedEventArgs e) => await SaveAsync();
    private async void Save_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs e)
    {
        e.Handled = true;
        await SaveAsync();
    }
    public void SetStack(bool canOlder, bool canNewer)
    {
        OlderButton.IsEnabled = canOlder;
        NewerButton.IsEnabled = canNewer;
    }

    private void Library_Click(object sender, RoutedEventArgs e) => App.Current.ShowLibrary();
    private async void Older_Click(object sender, RoutedEventArgs e) => await App.Current.FlipAsync(older: true);
    private async void Newer_Click(object sender, RoutedEventArgs e) => await App.Current.FlipAsync(older: false);
    private async void NewPage_Click(SplitButton sender, SplitButtonClickEventArgs e) => await App.Current.CreateNoteAsync();
    private async void NewPage_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs e)
    {
        e.Handled = true;
        await App.Current.CreateNoteAsync();
    }
    private async void TearOff_Click(object sender, RoutedEventArgs e) => await App.Current.TearOffAsync();
    private void Settings_Click(object sender, RoutedEventArgs e) => App.Current.ShowSettings();

    private void InputTools_Click(object sender, RoutedEventArgs e) =>
        ShowBelowBar(_inputTools ??= new Flyout { Content = new InputToolsPanel() });

    private void PaperColor_Click(object sender, RoutedEventArgs e)
    {
        if (_paperMenu is not null)
            ShowBelowBar(_paperMenu);
    }

    // Under the bar, at its right end by the overflow button the choice was made from.
    private void ShowBelowBar(FlyoutBase flyout) =>
        flyout.ShowAt(Commands, new FlyoutShowOptions { Placement = FlyoutPlacementMode.BottomEdgeAlignedRight });

    private async void Delete_Click(object sender, RoutedEventArgs e) => await DeleteSelectedAsync();

    private async Task DeleteSelectedAsync()
    {
        if (_selectedText is null && _selectedInk.Count == 0)
            return;
        ContentDialog confirm = new()
        {
            XamlRoot = XamlRoot,
            Title = "Delete selected object?",
            Content = _selectedText is not null ? "Delete this text from the note?" : $"Delete {_selectedInk.Count} selected ink strokes?",
            PrimaryButtonText = "Delete",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close
        };
        if (await confirm.ShowAsync() != ContentDialogResult.Primary)
            return;
        TextNote? selectedText = _selectedText;
        FinishEditing();
        if (selectedText is not null)
        {
            _texts.Remove(selectedText);
            RenderText();
            Changed();
        }
        else
        {
            foreach (InkStroke stroke in Ink.InkPresenter.StrokeContainer.GetStrokes())
                stroke.Selected = _selectedInk.Contains(stroke.Id);
            Ink.InkPresenter.StrokeContainer.DeleteSelected();
            Changed(inkChanged: true);
        }
        ClearSelection();
    }

    private void Editor_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (_editing is not null || FocusManager.GetFocusedElement(XamlRoot) is TextBox)
            return;
        switch (e.Key)
        {
            case VirtualKey.Left: MoveSelection(-10, 0); break;
            case VirtualKey.Right: MoveSelection(10, 0); break;
            case VirtualKey.Up: MoveSelection(0, -10); break;
            case VirtualKey.Down: MoveSelection(0, 10); break;
            case VirtualKey.Delete when _selectedText is not null || _selectedInk.Count > 0: _ = DeleteSelectedAsync(); break;
            // With nothing selected, Escape falls through to the popup and puts it down.
            case VirtualKey.Escape when _selectedText is not null || _selectedInk.Count > 0: ClearSelection(); break;
            default: return;
        }
        e.Handled = true;
    }
}
