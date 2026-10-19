using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Graphics;
using WinUIEx;

namespace MiniInk;

// A note torn off the pad: its own resizable window that can be kept on top.
// Closing it saves the note and puts it back in the stack.
public sealed partial class StickyNoteWindow : WindowEx
{
    private readonly EditorPage _editor;
    private readonly DispatcherQueueTimer _layoutSave;
    private RectInt32 _bounds;
    private bool _allowClose;
    private bool _closing;
    public Guid NoteId { get; }
    public StickyPlacement Placement => new()
    {
        NoteId = NoteId, X = _bounds.X, Y = _bounds.Y, Width = _bounds.Width, Height = _bounds.Height, Pinned = IsAlwaysOnTop
    };

    public StickyNoteWindow(Note note, RectInt32 bounds, bool draft, bool pinned)
    {
        InitializeComponent();
        NoteId = note.Id;
        _editor = new EditorPage(note, draft, sticky: true);
        _editor.UsePenTools(PenTools);
        PageHost.Content = _editor;
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(Bar);
        AppWindow.TitleBar.PreferredHeightOption = TitleBarHeightOption.Tall;
        SystemBackdrop = new MicaBackdrop();
        // Like a paper note: just a close button, no minimize or maximize.
        IsMinimizable = false;
        IsMaximizable = false;
        MinWidth = 360;
        MinHeight = 240;
        IsAlwaysOnTop = pinned;
        // Named for Alt+Tab and the taskbar only; the note itself shows no title.
        Title = _editor.SavedTitle;
        UpdatePin();
        _bounds = bounds;
        AppWindow.MoveAndResize(bounds);
        _layoutSave = DispatcherQueue.CreateTimer();
        _layoutSave.Interval = TimeSpan.FromMilliseconds(600);
        _layoutSave.IsRepeating = false;
        _layoutSave.Tick += (_, _) => App.Current.SaveStickyLayout();
        AppWindow.Changed += AppWindow_Changed;
        AppWindow.Closing += AppWindow_Closing;
    }

    public Task<bool> PrepareToCloseAsync() => _editor.PrepareToCloseAsync();

    public void ShowAndActivate()
    {
        if (AppWindow.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Minimized } presenter)
            presenter.Restore();
        Activate();
        BringToFront();
    }

    // Restored at startup without taking focus from whatever the user is doing.
    public void ShowInactive() => AppWindow.Show(activateWindow: false);

    public void CloseWindow()
    {
        if (_allowClose)
            return;
        _allowClose = true;
        _layoutSave.Stop();
        _editor.Detach();
        AppWindow.Changed -= AppWindow_Changed;
        AppWindow.Closing -= AppWindow_Closing;
        PageHost.Content = null;
        Close();
    }

    private async void AppWindow_Closing(AppWindow sender, AppWindowClosingEventArgs e)
    {
        if (_allowClose)
            return;
        e.Cancel = true;
        if (_closing)
            return;
        _closing = true;
        try
        {
            if (await PrepareToCloseAsync())
                CloseWindow();
        }
        finally
        {
            _closing = false;
        }
    }

    private void AppWindow_Changed(AppWindow sender, AppWindowChangedEventArgs args)
    {
        if (!args.DidPositionChange && !args.DidSizeChange ||
            AppWindow.Presenter is OverlappedPresenter { State: not OverlappedPresenterState.Restored })
            return;
        _bounds = new RectInt32(AppWindow.Position.X, AppWindow.Position.Y, AppWindow.Size.Width, AppWindow.Size.Height);
        _layoutSave.Stop();
        _layoutSave.Start();
    }

    private void Pin_Click(object sender, RoutedEventArgs e)
    {
        IsAlwaysOnTop = PinButton.IsChecked == true;
        UpdatePin();
        App.Current.SaveStickyLayout();
    }
    private void UpdatePin()
    {
        PinButton.IsChecked = IsAlwaysOnTop;
        PinIcon.Glyph = IsAlwaysOnTop ? "" : "";
        string label = IsAlwaysOnTop ? "Stop keeping on top" : "Keep on top";
        AutomationProperties.SetName(PinButton, label);
        ToolTipService.SetToolTip(PinButton, label);
    }

    private async void ReturnToStack_Click(object sender, RoutedEventArgs e) => await App.Current.ReturnToStackAsync(NoteId);
}
