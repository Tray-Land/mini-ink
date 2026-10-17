using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.UI;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.Foundation;
using Windows.Storage.Streams;
using Windows.System;
using Windows.UI.Core;
using Windows.UI.Input.Inking;
using Windows.UI.Input.Inking.Analysis;

namespace MiniInk;

public sealed partial class EditorPage : Page
{
    private readonly Note _note;
    private readonly List<TextNote> _texts;
    private readonly SemaphoreSlim _operations = new(1, 1);
    private readonly DispatcherTimer _autosave = new() { Interval = TimeSpan.FromSeconds(1.2) };
    private List<InkGroup> _groups = [];
    private HashSet<uint> _selectedInk = [];
    private TextNote? _selectedText;
    private TextNote? _editing;
    private long _revision;
    private long _savedRevision;
    private long _analyzedRevision = -1;
    private bool _loaded;
    private bool _loadFailed;
    private bool _updatingText;
    private bool _detached;
    private bool _closing;
    private bool _inking;
    private bool _suspended;
    private bool _isDraft;
    private string _titleText;
    private readonly TaskCompletionSource _loadComplete = new();
    private bool _pointerDown;
    private bool _dragging;
    private Point _pressed;
    private Point _last;
    private uint _pointerId;
    private PointerDeviceType _pointerType;
    private sealed record InkGroup(Rect Bounds, HashSet<uint> StrokeIds);

    public Guid NoteId => _note.Id;
    // A blank page that has not been written to disk yet.
    public bool IsDraft => _isDraft;
    public bool IsBlankPage => _isDraft && string.IsNullOrWhiteSpace(NoteTitle.Text) &&
        _texts.All(t => string.IsNullOrWhiteSpace(t.Text)) && Ink.InkPresenter.StrokeContainer.GetStrokes().Count == 0;
    public string SavedTitle => string.IsNullOrWhiteSpace(NoteTitle.Text) ? Note.Untitled : NoteTitle.Text.Trim();
    public event EventHandler? TitleChanged;
    // Raised when a blank page is first written to disk.
    public event EventHandler? Written;

    public EditorPage(Note note, bool draft = false, bool sticky = false)
    {
        _note = note;
        _isDraft = draft;
        _texts = note.Texts.Select(t => new TextNote { Id = t.Id, Text = t.Text, X = t.X, Y = t.Y }).ToList();
        InitializeComponent();
        _titleText = note.Title == Note.Untitled ? "" : note.Title;
        NoteTitle.Text = _titleText;
        if (sticky)
            LibraryButton.Visibility = StackActions.Visibility = Hint.Visibility = StackPosition.Visibility = Visibility.Collapsed;
        Ink.InkPresenter.InputDeviceTypes = CoreInputDeviceTypes.Pen;
        Ink.InkPresenter.UpdateDefaultDrawingAttributes(new InkDrawingAttributes
        {
            Color = Colors.DodgerBlue,
            Size = new Size(3, 3),
            IgnorePressure = false
        });
        PenTools.Loaded += (_, _) =>
        {
            Ink.InkPresenter.InputDeviceTypes = CoreInputDeviceTypes.Pen;
            InkDrawingAttributes attributes = Ink.InkPresenter.CopyDefaultDrawingAttributes();
            attributes.Color = Colors.DodgerBlue;
            Ink.InkPresenter.UpdateDefaultDrawingAttributes(attributes);
        };
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
        TextEditor.PointerEntered += (_, e) =>
        {
            if (e.Pointer.PointerDeviceType == PointerDeviceType.Pen)
                FinishEditing();
        };
        KeyDown += Editor_KeyDown;
        _autosave.Tick += Autosave_Tick;
        Loaded += Editor_Loaded;
    }

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
            Status.Text = _isDraft ? "Blank page - kept once you write on it" : "Saved on this device";
            if (!string.IsNullOrEmpty(_note.IndexError))
                ShowError($"Handwriting index needs a retry: {_note.IndexError}");
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
        {
            _groups.Clear();
            _analyzedRevision = -1;
        }
        Status.Text = "Unsaved changes";
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
        Size content = ContentSize();
        Surface.Width = Math.Max(content.Width, CanvasScroll.ActualWidth);
        Surface.Height = Math.Max(content.Height, CanvasScroll.ActualHeight);
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
        TitleChanged?.Invoke(this, EventArgs.Empty);
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
        if (e.Pointer.PointerDeviceType == PointerDeviceType.Pen)
        {
            FinishEditing();
            ClearSelection();
            return;
        }
        var current = e.GetCurrentPoint(Surface);
        if (e.Pointer.PointerDeviceType == PointerDeviceType.Mouse && !current.Properties.IsLeftButtonPressed)
            return;
        Point point = current.Position;
        if (e.Pointer.PointerDeviceType == PointerDeviceType.Mouse && _editing is not null &&
            new Rect(_editing.X, _editing.Y, TextEditor.ActualWidth, TextEditor.ActualHeight).Contains(point))
            return;
        FinishEditing();
        _pointerDown = true;
        _pointerId = e.Pointer.PointerId;
        _pointerType = e.Pointer.PointerDeviceType;
        _pressed = _last = point;
        _dragging = false;
        TextNote? text = HitText(point);
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

    private async void Surface_PointerReleased(object sender, PointerRoutedEventArgs e)
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
        if (_selectedText is not null)
        {
            if (_pointerType == PointerDeviceType.Mouse)
                EditText(_selectedText);
            return;
        }
        bool onInk = Ink.InkPresenter.StrokeContainer.GetStrokes()
            .Any(s => Inflate(s.BoundingRect, 12).Contains(point));
        if (onInk || _pointerType == PointerDeviceType.Touch)
        {
            await AnalyzeAndSaveAsync(point);
            return;
        }
        AddText(point);
    }

    private void Surface_PointerCanceled(object sender, PointerRoutedEventArgs e)
    {
        if (e.Pointer.PointerId == _pointerId)
            _pointerDown = false;
    }

    private static Rect Inflate(Rect rect, double padding) => new(
        rect.X - padding, rect.Y - padding, rect.Width + padding * 2, rect.Height + padding * 2);

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
            return await SaveCoreAsync(analyze: true);
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

    private async Task<bool> SaveCoreAsync(bool analyze)
    {
        while (_inking || _pointerDown)
            await Task.Delay(50);
        long revision = _revision;
        bool needsAnalysis = _analyzedRevision != revision || !string.IsNullOrEmpty(_note.IndexError);
        Size content = ContentSize();
        Note snapshot = new()
        {
            Id = _note.Id,
            Title = SavedTitle,
            Updated = DateTimeOffset.Now,
            Texts = _texts.Where(t => !string.IsNullOrWhiteSpace(t.Text))
                .Select(t => new TextNote { Id = t.Id, Text = t.Text, X = t.X, Y = t.Y }).ToList(),
            RecognizedText = needsAnalysis ? "" : _note.RecognizedText,
            HandwritingKeywords = needsAnalysis ? "" : _note.HandwritingKeywords,
            IndexError = _note.IndexError,
            CanvasWidth = content.Width,
            CanvasHeight = content.Height,
            StrokeCount = Ink.InkPresenter.StrokeContainer.GetStrokes().Count
        };
        // Freeze only the short ISF snapshot, not the slower recognition operation.
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
        // Ink is durable before recognition, even if handwriting support is unavailable.
        if (needsAnalysis)
            snapshot.IndexError = "Handwriting analysis is pending. Tap Analyze ink to retry.";
        await App.Current.Notes.SaveAsync(snapshot);
        _savedRevision = revision;
        if (_isDraft)
        {
            _isDraft = false;
            Written?.Invoke(this, EventArgs.Empty);
        }
        _note.InkBase64 = snapshot.InkBase64;
        _note.Title = snapshot.Title;
        _note.IndexError = snapshot.IndexError;
        if (analyze && _revision == revision && needsAnalysis)
        {
            try
            {
                Status.Text = "Saved; analyzing handwriting...";
                InkAnalyzer analyzer = new();
                var strokes = Ink.InkPresenter.StrokeContainer.GetStrokes();
                if (strokes.Count > 0)
                {
                    analyzer.AddDataForStrokes(strokes);
                    InkAnalysisResult result = await analyzer.AnalyzeAsync();
                    if (result.Status != InkAnalysisStatus.Updated)
                        throw new InvalidOperationException($"Ink analysis returned {result.Status}. Check Windows handwriting language support.");
                }
                if (_revision == revision)
                {
                    _groups = analyzer.AnalysisRoot.FindNodes(InkAnalysisNodeKind.InkWord)
                        .Concat(analyzer.AnalysisRoot.FindNodes(InkAnalysisNodeKind.InkDrawing))
                        .Select(n => new InkGroup(n.BoundingRect, n.GetStrokeIds().ToHashSet())).ToList();
                    HashSet<uint> grouped = _groups.SelectMany(g => g.StrokeIds).ToHashSet();
                    _groups.AddRange(strokes.Where(s => !grouped.Contains(s.Id))
                        .Select(s => new InkGroup(s.BoundingRect, [s.Id])));
                    InkAnalysisInkWord[] words = analyzer.AnalysisRoot.FindNodes(InkAnalysisNodeKind.InkWord)
                        .OfType<InkAnalysisInkWord>().ToArray();
                    snapshot.RecognizedText = string.Join(" ", words.Select(w => w.RecognizedText));
                    snapshot.HandwritingKeywords = string.Join(" ", words.SelectMany(w => w.TextAlternates).Distinct());
                    _analyzedRevision = revision;
                    if (strokes.Count > 0)
                    {
                        // Index recognizer alternatives too, without cluttering the note preview.
                        InkRecognizerContainer recognizer = new();
                        if (recognizer.GetRecognizers().Count == 0)
                            throw new InvalidOperationException("Install a handwriting language in Windows Settings to index this ink.");
                        Windows.UI.Input.Inking.InkStrokeContainer recognitionInk = new();
                        recognitionInk.AddStrokes(strokes.Select(s => s.Clone()));
                        var results = await recognizer.RecognizeAsync(recognitionInk, InkRecognitionTarget.All);
                        if (_revision != revision)
                            return true;
                        if (string.IsNullOrWhiteSpace(snapshot.RecognizedText))
                            snapshot.RecognizedText = string.Join(" ", results.Select(r => r.GetTextCandidates().FirstOrDefault() ?? ""));
                        snapshot.HandwritingKeywords = string.Join(" ",
                            new[] { snapshot.HandwritingKeywords, snapshot.RecognizedText }
                                .Concat(results.SelectMany(r => r.GetTextCandidates())).Distinct());
                    }
                    snapshot.IndexError = "";
                    await App.Current.Notes.SaveAsync(snapshot);
                    _note.RecognizedText = snapshot.RecognizedText;
                    _note.HandwritingKeywords = snapshot.HandwritingKeywords;
                    _note.IndexError = "";
                    Error.IsOpen = false;
                }
            }
            catch (Exception ex) when (ex is COMException or InvalidOperationException)
            {
                snapshot.IndexError = ex.Message;
                await App.Current.Notes.SaveAsync(snapshot);
                _note.IndexError = ex.Message;
                ShowError($"Ink was saved, but handwriting could not be indexed: {ex.Message}", ex);
            }
        }
        if (_revision != revision)
        {
            Status.Text = "Unsaved changes";
            _autosave.Stop();
            if (!_suspended)
                _autosave.Start();
        }
        else
        {
            Status.Text = string.IsNullOrEmpty(_note.IndexError)
                ? $"Saved on this device - {Ink.InkPresenter.StrokeContainer.GetStrokes().Count} ink strokes"
                : "Saved - handwriting search needs a retry";
        }
        return true;
    }

    private async Task AnalyzeAndSaveAsync(Point? point = null)
    {
        if (!_loaded || _loadFailed)
            return;
        await _operations.WaitAsync();
        try
        {
            if (!await SaveCoreAsync(analyze: true))
                return;
            if (point is null || _analyzedRevision != _revision)
                return;
            ClearSelection();
            InkGroup? group = _groups.Where(g => Inflate(g.Bounds, 12).Contains(point.Value))
                .OrderBy(g => g.Bounds.Width * g.Bounds.Height).FirstOrDefault();
            if (group is not null)
            {
                _selectedInk = group.StrokeIds.ToHashSet();
                UpdateSelection();
                Status.Text = "Ink group selected - drag with a finger to move it";
            }
        }
        catch (Exception ex)
        {
            ShowError($"Could not analyze this note: {ex.Message}", ex);
        }
        finally
        {
            _operations.Release();
        }
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
            // Let an in-flight save or analysis finish, then write only what changed,
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
    }

    public void ShowError(string message, Exception? ex = null)
    {
        Debug.WriteLine($"MiniInk editor: {message}\n{ex}");
        Error.Message = message;
        Error.IsOpen = true;
        Status.Text = message;
    }

    private void AddText_Click(object sender, RoutedEventArgs e)
    {
        if (_loaded && !_loadFailed)
            AddText(new Point(24, 24));
    }
    private async void Analyze_Click(object sender, RoutedEventArgs e) => await AnalyzeAndSaveAsync();
    private async void Save_Click(object sender, RoutedEventArgs e) => await SaveAsync();
    private async void Save_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs e)
    {
        e.Handled = true;
        await SaveAsync();
    }
    public void SetStack(string position, bool canOlder, bool canNewer)
    {
        StackPosition.Text = position;
        OlderButton.IsEnabled = canOlder;
        NewerButton.IsEnabled = canNewer;
    }

    private void Library_Click(object sender, RoutedEventArgs e) => App.Current.ShowLibrary();
    private async void Older_Click(object sender, RoutedEventArgs e) => await App.Current.FlipAsync(older: true);
    private async void Newer_Click(object sender, RoutedEventArgs e) => await App.Current.FlipAsync(older: false);
    private async void NewPage_Click(object sender, RoutedEventArgs e) => await App.Current.CreateNoteAsync();
    private async void TearOff_Click(object sender, RoutedEventArgs e) => await App.Current.TearOffAsync();

    private async void Delete_Click(object sender, RoutedEventArgs e)
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
            // With nothing selected, Escape falls through to the popup and puts it down.
            case VirtualKey.Escape when _selectedText is not null || _selectedInk.Count > 0: ClearSelection(); break;
            default: return;
        }
        e.Handled = true;
    }
}
