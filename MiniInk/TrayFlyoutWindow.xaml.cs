using System.Diagnostics;
using System.Globalization;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;
using Windows.Graphics;
using Windows.UI.ViewManagement;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Dwm;
using Windows.Win32.UI.WindowsAndMessaging;
using WinUIEx;

namespace MiniInk;

public sealed partial class TrayFlyoutWindow : WindowEx
{
    private readonly HWND _hwnd;
    private readonly MainPage _page = new();
    private SettingsPage? _settingsPage;
    private EditorPage? _editor;
    private Guid? _editorId;
    private readonly NoteStack _stack = new();
    private readonly SemaphoreSlim _navigation = new(1, 1);
    private bool _restored;
    private readonly ShellBackdrop _backdrop = new();
    private readonly UISettings _settings = new();
    private readonly DispatcherQueueTimer _dismiss;
    private readonly DispatcherQueueTimer _idleClose;
    private readonly DispatcherQueueTimer _animation;
    private readonly Stopwatch _clock = new();
    private bool _visible;
    private bool _closed;
    private bool _allowClose;
    private bool _hiding;
    private DateTime _lastDismiss = DateTime.MinValue;
    private int _baseX, _baseY, _fromOffset, _toOffset, _offset;
    private double _fromAlpha, _toAlpha, _alpha, _duration;

    public TrayFlyoutWindow()
    {
        InitializeComponent();
        _hwnd = (HWND)WinRT.Interop.WindowNative.GetWindowHandle(this);
        PageHost.Content = _page;
        SystemBackdrop = _backdrop;
        ApplyTheme();
        _settings.ColorValuesChanged += ColorValuesChanged;
        SystemThemeService.Changed += ThemeChanged;
        OverlappedPresenter presenter = OverlappedPresenter.Create();
        presenter.IsMaximizable = presenter.IsMinimizable = presenter.IsResizable = false;
        presenter.IsAlwaysOnTop = true;
        presenter.SetBorderAndTitleBar(true, false);
        AppWindow.SetPresenter(presenter);
        AppWindow.IsShownInSwitchers = false;
        int style = PInvoke.GetWindowLong(_hwnd, WINDOW_LONG_PTR_INDEX.GWL_EXSTYLE);
        PInvoke.SetWindowLong(_hwnd, WINDOW_LONG_PTR_INDEX.GWL_EXSTYLE, style | 0x80 | 0x80000);
        unsafe
        {
            DWM_WINDOW_CORNER_PREFERENCE corner = DWM_WINDOW_CORNER_PREFERENCE.DWMWCP_ROUND;
            PInvoke.DwmSetWindowAttribute(_hwnd, DWMWINDOWATTRIBUTE.DWMWA_WINDOW_CORNER_PREFERENCE, &corner, sizeof(DWM_WINDOW_CORNER_PREFERENCE));
        }
        _dismiss = DispatcherQueue.CreateTimer();
        _dismiss.Interval = TimeSpan.FromMilliseconds(150);
        _dismiss.IsRepeating = false;
        _dismiss.Tick += (_, _) => CheckFocus();
        _idleClose = DispatcherQueue.CreateTimer();
        _idleClose.Interval = TimeSpan.FromMinutes(1);
        _idleClose.IsRepeating = false;
        _idleClose.Tick += async (_, _) =>
        {
            if (!_visible && await PrepareToCloseAsync() && !_visible)
                CloseWindow();
        };
        _animation = DispatcherQueue.CreateTimer();
        _animation.Interval = TimeSpan.FromMilliseconds(15);
        _animation.Tick += Animate;
        Activated += WindowActivated;
        AppWindow.Closing += WindowClosing;
    }

    public void Toggle()
    {
        if (_visible)
            HidePopup();
        else if (DateTime.UtcNow - _lastDismiss > TimeSpan.FromMilliseconds(300))
            Summon();
    }
    // Opening from the tray: a blank page after a while away, otherwise whatever was in use, but never settings.
    public void Summon()
    {
        bool fresh = !_visible && App.Current.FreshPageDue;
        LeaveSettings();
        ShowPopup(fresh);
        RefreshBlankPage();
    }
    public void ShowPopup(bool fresh = false)
    {
        if (_closed)
            return;
        _dismiss.Stop();
        _idleClose.Stop();
        WindowPlacementService.PositionNearTray(this, 440, 620);
        _baseX = AppWindow.Position.X;
        _baseY = AppWindow.Position.Y;
        ApplyTheme();
        _visible = true;
        StartAnimation(false);
        this.Show();
        Activate();
        PInvoke.SetForegroundWindow(_hwnd);
        RestorePage(fresh);
    }
    public void HidePopup()
    {
        _dismiss.Stop();
        if (!_visible)
            return;
        _visible = false;
        _lastDismiss = DateTime.UtcNow;
        App.Current.LastUsed = DateTimeOffset.UtcNow;
        _page.OnHidden();
        if (_editor is not null)
            SaveOnDismiss();
        StartAnimation(true);
        _idleClose.Start();
    }
    public void ShowError(string message)
    {
        if (_editor is not null)
            _editor.ShowError(message);
        else
            _page.ShowError(message);
    }
    private async void SaveOnDismiss()
    {
        if (_editor is not null)
            await _editor.SuspendAsync();
    }
    private async void RestorePage(bool fresh)
    {
        try
        {
            await App.Current.Notes.LoadAsync();
            _stack.Reset(App.Current.Notes.All, App.Current.DesktopNoteIds);
            if (fresh)
            {
                _restored = true;
                if (_editor is not { IsBlankPage: true })
                    await OpenNoteAsync(new Note(), show: false, draft: true);
            }
            else if (!_restored)
            {
                _restored = true;
                // No active note means the library was showing. A note that was never
                // written, or now lives on the desktop, gives way to a blank page.
                Guid? id = App.Current.ActiveNoteId;
                Note? active = id is Guid value && !App.Current.IsOnDesktop(value) ? App.Current.Notes.Find(value) : null;
                if (active is not null)
                    await OpenNoteAsync(active, show: false);
                else if (id is not null)
                    await OpenNoteAsync(new Note(), show: false, draft: true);
            }
            UpdateStack();
            if (_visible)
            {
                if (_editor is not null)
                    _editor.Resume();
                else if (!IsShowingSettings)
                    _page.OnShown();
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"MiniInk: restoring tray page failed: {ex}");
            _page.ShowError($"Could not restore the note: {ex.Message}");
        }
    }
    public async Task OpenNoteAsync(Note note, bool show = true, bool draft = false)
    {
        await _navigation.WaitAsync();
        try
        {
            if (_closed)
                return;
            if (_editorId != note.Id)
            {
                if (!await LeaveEditorAsync())
                    return;
                ShowEditor(note, draft);
            }
            if (show)
            {
                LeaveSettings();
                ShowPopup();
            }
        }
        finally
        {
            _navigation.Release();
        }
    }
    public async Task NewPageAsync(NoteTemplate template = NoteTemplate.None)
    {
        await _navigation.WaitAsync();
        try
        {
            if (_closed)
                return;
            Note page = NoteTemplates.Create(template, DateTime.Now, CultureInfo.CurrentCulture, App.Current.TemplateOptions);
            // An unwritten page is reused when it is already on the paper asked for.
            if (_editor is not { IsBlankPage: true } || _editor.NoteTemplate != template || _editor.TemplateOptions?.Unfitted != page.Options)
            {
                if (!await LeaveEditorAsync())
                    return;
                ShowEditor(page, draft: true);
            }
            LeaveSettings();
            ShowPopup();
        }
        finally
        {
            _navigation.Release();
        }
    }
    public async Task FlipAsync(bool older)
    {
        await _navigation.WaitAsync();
        try
        {
            if (_closed || _editor is null || !await LeaveEditorAsync())
                return;
            await App.Current.Notes.LoadAsync();
            _stack.Retain(id => App.Current.Notes.Find(id) is not null && !App.Current.IsOnDesktop(id));
            Guid current = _editor.NoteId;
            if (older ? _stack.CanGoOlder(current) : _stack.CanGoNewer(current))
            {
                Guid? target = older ? _stack.Older(current) : _stack.Newer(current);
                Note? next = target is Guid id ? App.Current.Notes.Find(id) : null;
                ShowEditor(next ?? new Note(), draft: next is null);
            }
            else
                UpdateStack();
        }
        finally
        {
            _navigation.Release();
        }
    }
    // Lifts the open note off the pad into its own window, leaving a blank page behind.
    public async Task TearOffAsync()
    {
        await _navigation.WaitAsync();
        try
        {
            if (_closed || _editor is null || !await _editor.PrepareToCloseAsync())
                return;
            // A blank page leaves as a blank sticky on the same paper; it is written once something is on it.
            bool draft = _editor.IsDraft;
            Note? note = draft ? NoteTemplates.Create(_editor.NoteTemplate, DateTime.Now, CultureInfo.CurrentCulture, _editor.TemplateOptions)
                : App.Current.Notes.Find(_editor.NoteId);
            if (note is null)
                return;
            RectInt32 bounds = new(_baseX, _baseY, AppWindow.Size.Width, AppWindow.Size.Height);
            _stack.Retain(id => id != note.Id);
            ShowEditor(new Note(), draft: true);
            HidePopup();
            App.Current.OpenSticky(note, bounds, draft: draft);
        }
        finally
        {
            _navigation.Release();
        }
    }
    // Saves the open note; false keeps it open after a failed save.
    private async Task<bool> LeaveEditorAsync() => _editor is null || await _editor.PrepareToCloseAsync();
    // A blank page that gets written on joins the top of the pile.
    private void Editor_Written(object? sender, EventArgs e)
    {
        if (sender != _editor || _editor is null)
            return;
        _stack.Remember(_editor.NoteId);
        UpdateStack();
    }
    private void ShowEditor(Note note, bool draft)
    {
        if (_editor is not null)
        {
            _editor.Written -= Editor_Written;
            _editor.Detach();
        }
        _page.OnHidden();
        _editor = new EditorPage(note, draft);
        _editor.Written += Editor_Written;
        _editorId = note.Id;
        _restored = true;
        // Restoring the last note behind settings leaves settings showing.
        if (!IsShowingSettings)
            PageHost.Content = _editor;
        App.Current.ActiveNoteId = note.Id;
        UpdateStack();
    }
    public bool IsShowingSettings => PageHost.Content is SettingsPage;
    public void ShowSettings()
    {
        if (_closed)
            return;
        _settingsPage ??= new SettingsPage(LeaveSettings);
        if (!IsShowingSettings)
        {
            _page.OnHidden();
            PageHost.Content = _settingsPage;
        }
        _settingsPage.OnShown();
        if (!_visible)
            ShowPopup();
    }
    // Back to the note or the library that settings opened over.
    public void LeaveSettings()
    {
        if (!IsShowingSettings)
            return;
        if (_editor is not null)
        {
            PageHost.Content = _editor;
            RefreshBlankPage();
        }
        else
        {
            PageHost.Content = _page;
            if (_visible)
                _page.OnShown();
        }
    }
    // A blank planner or graph page made before its settings changed, or before the hour moved
    // on for one that starts at the current hour, starts over.
    private async void RefreshBlankPage()
    {
        await _navigation.WaitAsync();
        try
        {
            if (_closed || IsShowingSettings || _editor is not { IsBlankPage: true } editor)
                return;
            Note page = NoteTemplates.Create(editor.NoteTemplate, DateTime.Now, CultureInfo.CurrentCulture, App.Current.TemplateOptions);
            if (page.Options != editor.TemplateOptions?.Unfitted && await LeaveEditorAsync())
                ShowEditor(page, draft: true);
        }
        finally
        {
            _navigation.Release();
        }
    }
    private void UpdateStack() =>
        _editor?.SetStack(_stack.CanGoOlder(_editorId), _stack.CanGoNewer(_editorId));
    public async Task ShowLibraryAsync()
    {
        await _navigation.WaitAsync();
        try
        {
            if (!await LeaveEditorAsync())
                return;
            _editor?.Detach();
            _editor = null;
            _editorId = null;
            _restored = true;
            App.Current.ActiveNoteId = null;
            PageHost.Content = _page;
            ShowPopup();
        }
        finally
        {
            _navigation.Release();
        }
    }
    public async Task<bool> PrepareToCloseAsync()
    {
        await _navigation.WaitAsync();
        try
        {
            return _editor is null || await _editor.PrepareToCloseAsync();
        }
        finally
        {
            _navigation.Release();
        }
    }
    public void CloseWindow()
    {
        if (_closed)
            return;
        _closed = true;
        _dismiss.Stop();
        _idleClose.Stop();
        _animation.Stop();
        _page.OnHidden();
        _editor?.Detach();
        _editor = null;
        _settingsPage = null;
        _settings.ColorValuesChanged -= ColorValuesChanged;
        SystemThemeService.Changed -= ThemeChanged;
        Activated -= WindowActivated;
        AppWindow.Closing -= WindowClosing;
        PageHost.Content = null;
        _allowClose = true;
        Close();
    }
    private void ColorValuesChanged(UISettings sender, object args) => DispatcherQueue.TryEnqueue(ApplyTheme);
    private void ThemeChanged(object? sender, EventArgs e) => DispatcherQueue.TryEnqueue(ApplyTheme);
    private void ApplyTheme()
    {
        PopupRoot.RequestedTheme = SystemThemeService.IsLight ? ElementTheme.Light : ElementTheme.Dark;
        _backdrop.Update(SystemThemeService.IsLight,
            SystemThemeService.ShowsAccentColor ? _settings.GetColorValue(UIColorType.AccentDark2) : null);
    }
    private void StartAnimation(bool hiding)
    {
        _hiding = hiding;
        double scale = PInvoke.GetDpiForWindow(_hwnd) / 96.0;
        _fromOffset = hiding ? _offset : (int)(24 * scale);
        _toOffset = hiding ? (int)(12 * scale) : 0;
        _fromAlpha = hiding ? _alpha : 0;
        _toAlpha = hiding ? 0 : 1;
        _duration = hiding ? 180 : 250;
        ApplyFrame(_fromOffset, _fromAlpha);
        _clock.Restart();
        _animation.Start();
    }
    private void Animate(DispatcherQueueTimer sender, object args)
    {
        double progress = Math.Min(1, _clock.Elapsed.TotalMilliseconds / _duration);
        double eased = _hiding ? Math.Pow(progress, 3) : 1 - Math.Pow(1 - progress, 3);
        ApplyFrame(_fromOffset + (int)Math.Round((_toOffset - _fromOffset) * eased),
            _fromAlpha + (_toAlpha - _fromAlpha) * progress);
        if (progress < 1)
            return;
        _animation.Stop();
        if (_hiding)
            this.Hide();
    }
    private void ApplyFrame(int offset, double alpha)
    {
        _offset = offset;
        _alpha = alpha;
        PInvoke.SetLayeredWindowAttributes(_hwnd, new COLORREF(0), (byte)Math.Clamp(Math.Round(alpha * 255), 0, 255),
            LAYERED_WINDOW_ATTRIBUTES_FLAGS.LWA_ALPHA);
        AppWindow.Move(new PointInt32(_baseX, _baseY + offset));
    }
    private void WindowActivated(object sender, WindowActivatedEventArgs e)
    {
        if (e.WindowActivationState == WindowActivationState.Deactivated && _visible)
            _dismiss.Start();
        else
            _dismiss.Stop();
    }
    private void CheckFocus()
    {
        HWND foreground = PInvoke.GetForegroundWindow();
        if (foreground == _hwnd || PInvoke.GetAncestor(foreground, GET_ANCESTOR_FLAGS.GA_ROOT) == _hwnd)
            return;
        Span<char> buffer = stackalloc char[64];
        int length = PInvoke.GetClassName(foreground, buffer);
        ReadOnlySpan<char> name = buffer[..Math.Max(0, length)];
        // XAML popups (ComboBox lists, flyouts) are separate windows owned by this one.
        if (name.SequenceEqual("Xaml_WindowedPopupClass") || name.SequenceEqual("#32768") ||
            PInvoke.GetAncestor(foreground, GET_ANCESTOR_FLAGS.GA_ROOTOWNER) == _hwnd)
        {
            _dismiss.Start();
            return;
        }
        HidePopup();
    }
    private void WindowClosing(AppWindow sender, AppWindowClosingEventArgs e)
    {
        if (!_allowClose)
        {
            e.Cancel = true;
            HidePopup();
        }
    }
    // Escape steps back out of settings before it puts the popup down.
    private void Escape_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs e)
    {
        e.Handled = true;
        if (IsShowingSettings)
            LeaveSettings();
        else
            HidePopup();
    }
}
