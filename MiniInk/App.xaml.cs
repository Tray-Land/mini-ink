using System.Diagnostics;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Graphics;
using Windows.Storage;
using Windows.Win32;
using WinUIEx;

namespace MiniInk;

public partial class App : Application
{
    public static new App Current => (App)Application.Current;
    public NoteStore Notes { get; } = new(Path.Combine(ApplicationData.Current.LocalFolder.Path, "Notes"));
    private const string MutexName = @"Local\MiniInk.Primary";
    private const string ShowEventName = @"Local\MiniInk.Show";
    public Guid? ActiveNoteId
    {
        get => ApplicationData.Current.LocalSettings.Values["ActiveNote"] is string id && Guid.TryParse(id, out Guid value) ? value : null;
        set
        {
            if (value is Guid id)
                ApplicationData.Current.LocalSettings.Values["ActiveNote"] = id.ToString();
            else
                ApplicationData.Current.LocalSettings.Values.Remove("ActiveNote");
        }
    }
    // When the popup was last put down; the fresh-page timer counts from here.
    public DateTimeOffset? LastUsed
    {
        get => ApplicationData.Current.LocalSettings.Values["LastUsed"] is long ticks ? new DateTimeOffset(ticks, TimeSpan.Zero) : null;
        set
        {
            if (value is DateTimeOffset used)
                ApplicationData.Current.LocalSettings.Values["LastUsed"] = used.UtcTicks;
            else
                ApplicationData.Current.LocalSettings.Values.Remove("LastUsed");
        }
    }
    public int FreshAfterMinutes
    {
        get => ApplicationData.Current.LocalSettings.Values["FreshAfterMinutes"] is int minutes && FreshPagePolicy.Choices.Contains(minutes)
            ? minutes : FreshPagePolicy.DefaultMinutes;
        set => ApplicationData.Current.LocalSettings.Values["FreshAfterMinutes"] = value;
    }
    public bool FreshPageDue => FreshPagePolicy.ShouldStartFresh(LastUsed, DateTimeOffset.UtcNow, FreshAfterMinutes);
    // What new planner and graph pages are made with.
    public TemplateOptions TemplateOptions
    {
        get => TemplateOptions.Parse(ApplicationData.Current.LocalSettings.Values["TemplateOptions"] as string);
        set => ApplicationData.Current.LocalSettings.Values["TemplateOptions"] = value.ToJson();
    }
    // The paper a new note starts on when none is picked from the New page menu.
    public NoteTemplate DefaultTemplate
    {
        get => ApplicationData.Current.LocalSettings.Values["DefaultTemplate"] is int value && Enum.IsDefined((NoteTemplate)value)
            ? (NoteTemplate)value : NoteTemplate.None;
        set => ApplicationData.Current.LocalSettings.Values["DefaultTemplate"] = (int)value;
    }
    // The paper color a new note starts with.
    public NoteColor DefaultPaper
    {
        get => ApplicationData.Current.LocalSettings.Values["DefaultPaper"] is int value && Enum.IsDefined((NoteColor)value)
            ? (NoteColor)value : NoteColor.Default;
        set => ApplicationData.Current.LocalSettings.Values["DefaultPaper"] = (int)value;
    }
    // What the mouse and touch do on the canvas, shared by the popup and every sticky note.
    public InputTool MouseTool
    {
        get => InputTools.Parse(ApplicationData.Current.LocalSettings.Values["MouseTool"], InputTools.DefaultMouse);
        set
        {
            ApplicationData.Current.LocalSettings.Values["MouseTool"] = (int)value;
            InputToolsChanged?.Invoke(this, EventArgs.Empty);
        }
    }
    public InputTool TouchTool
    {
        get => InputTools.Parse(ApplicationData.Current.LocalSettings.Values["TouchTool"], InputTools.DefaultTouch);
        set
        {
            ApplicationData.Current.LocalSettings.Values["TouchTool"] = (int)value;
            InputToolsChanged?.Invoke(this, EventArgs.Empty);
        }
    }
    public event EventHandler? InputToolsChanged;
    private readonly StickyLayoutStore _layout = new(Path.Combine(ApplicationData.Current.LocalFolder.Path, "StickyNotes.json"));
    private readonly Dictionary<Guid, StickyNoteWindow> _stickies = [];
    public IReadOnlySet<Guid> DesktopNoteIds => _stickies.Keys.ToHashSet();
    public bool IsOnDesktop(Guid id) => _stickies.ContainsKey(id);
    private Mutex? _instance;
    private EventWaitHandle? _showEvent;
    private RegisteredWaitHandle? _showWait;
    private DispatcherQueue? _dispatcher;
    private TrayIcon? _tray;
    private TrayFlyoutWindow? _flyout;
    private bool _exiting;
    private bool _exitPending;

    public App()
    {
        InitializeComponent();
        DispatcherShutdownMode = DispatcherShutdownMode.OnExplicitShutdown;
        UnhandledException += (_, e) => LogCrash(e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, e) => LogCrash(e.ExceptionObject as Exception);
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        _instance = new Mutex(true, MutexName, out bool primary);
        if (!primary)
        {
            try
            {
                PInvoke.AllowSetForegroundWindow(uint.MaxValue);
                using EventWaitHandle show = EventWaitHandle.OpenExisting(ShowEventName);
                show.Set();
            }
            catch (Exception ex) when (ex is WaitHandleCannotBeOpenedException or UnauthorizedAccessException)
            {
                LogCrash(ex);
            }
            _instance.Dispose();
            Exit();
            return;
        }
        _dispatcher = DispatcherQueue.GetForCurrentThread();
        _showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName);
        _showWait = ThreadPool.RegisterWaitForSingleObject(_showEvent,
            (_, _) => _dispatcher.TryEnqueue(ShowPopup), null, Timeout.Infinite, false);
        _tray = new TrayIcon(1, TrayIconPath, "MiniInk - quick notes");
        _tray.Selected += (_, _) => EnsureFlyout().Toggle();
        _tray.ContextMenu += OnTrayContextMenu;
        _tray.IsVisible = true;
        WindowPlacementService.SetTrayIcon(_tray);
        SystemThemeService.Changed += Theme_Changed;
        Notes.Changed += Notes_Changed;
        RestoreStickies();
        if (ApplicationData.Current.LocalSettings.Values["HasLaunched"] is not true)
        {
            ApplicationData.Current.LocalSettings.Values["HasLaunched"] = true;
            ShowPopup();
        }
        else
        {
            MemoryService.ReleaseIdle(() => _flyout is null, TimeSpan.FromSeconds(30));
        }
    }

    private static string TrayIconPath => Path.Combine(AppContext.BaseDirectory, "Assets",
        SystemThemeService.IsLight ? "AppIcon-dark.ico" : "AppIcon.ico");
    private void Theme_Changed(object? sender, EventArgs e) =>
        _dispatcher?.TryEnqueue(() => _tray?.SetIcon(TrayIconPath));
    private void Notes_Changed(object? sender, EventArgs e)
    {
        if (_tray is not null)
            _tray.Tooltip = $"MiniInk - {Notes.All.Count} notes - click to search or write";
    }

    private TrayFlyoutWindow EnsureFlyout()
    {
        if (_flyout is null)
        {
            _flyout = new TrayFlyoutWindow();
            _flyout.Closed += (_, _) =>
            {
                _flyout = null;
                ReleaseIdle();
            };
        }
        return _flyout;
    }

    private void OnTrayContextMenu(TrayIcon sender, TrayIconEventArgs args)
    {
        MenuFlyout menu = new();
        MenuFlyoutItem create = new() { Text = "New note", Icon = new SymbolIcon(Symbol.Add) };
        create.Click += async (_, _) => await CreateNoteAsync();
        MenuFlyoutItem sticky = new() { Text = "New sticky note", Icon = new FontIcon { Glyph = "" } };
        sticky.Click += (_, _) => CreateSticky();
        MenuFlyoutItem open = new() { Text = "Open MiniInk", Icon = new SymbolIcon(Symbol.OpenPane) };
        open.Click += (_, _) => ShowPopup();
        MenuFlyoutItem settings = new() { Text = "Settings", Icon = new SymbolIcon(Symbol.Setting) };
        settings.Click += (_, _) => ShowSettings();
        MenuFlyoutItem exit = new() { Text = "Exit MiniInk", Icon = new SymbolIcon(Symbol.Cancel) };
        exit.Click += async (_, _) => await ExitAsync();
        menu.Items.Add(open);
        menu.Items.Add(create);
        menu.Items.Add(sticky);
        if (_stickies.Count > 0)
        {
            MenuFlyoutItem show = new()
            {
                Text = _stickies.Count == 1 ? "Show sticky note" : $"Show {_stickies.Count} sticky notes",
                Icon = new SymbolIcon(Symbol.Pin)
            };
            show.Click += (_, _) => ShowStickies();
            menu.Items.Add(show);
        }
        menu.Items.Add(new MenuFlyoutSeparator());
        menu.Items.Add(settings);
        menu.Items.Add(exit);
        args.Flyout = menu;
    }

    public void ShowPopup()
    {
        if (!_exiting)
            EnsureFlyout().Summon();
    }

    public async void ShowLibrary()
    {
        if (!_exiting)
            await EnsureFlyout().ShowLibraryAsync();
    }

    // Settings open in the popup, in place of the library or the note until going back.
    public void ShowSettings()
    {
        if (_exiting)
            return;
        _tray?.CloseFlyout();
        EnsureFlyout().ShowSettings();
    }

    public async Task CreateNoteAsync(NoteTemplate? template = null)
    {
        if (_exitPending)
            return;
        _tray?.CloseFlyout();
        await EnsureFlyout().NewPageAsync(template ?? DefaultTemplate);
    }

    public async Task OpenNoteAsync(Note note)
    {
        if (_exitPending)
            return;
        _tray?.CloseFlyout();
        if (_stickies.TryGetValue(note.Id, out StickyNoteWindow? sticky))
        {
            _flyout?.HidePopup();
            sticky.ShowAndActivate();
            return;
        }
        await EnsureFlyout().OpenNoteAsync(note);
    }

    // Deleting a note on the desktop also closes its sticky window, without saving it.
    public async Task DeleteNoteAsync(Guid id)
    {
        if (_stickies.TryGetValue(id, out StickyNoteWindow? sticky))
            sticky.CloseWindow();
        await Notes.DeleteAsync(id);
    }

    public async Task FlipAsync(bool older)
    {
        if (!_exitPending && _flyout is not null)
            await _flyout.FlipAsync(older);
    }

    public async Task TearOffAsync()
    {
        if (!_exitPending && _flyout is not null)
            await _flyout.TearOffAsync();
    }

    public void OpenSticky(Note note, RectInt32 bounds, bool draft = false, bool pinned = false, bool activate = true)
    {
        if (_exiting)
            return;
        if (_stickies.TryGetValue(note.Id, out StickyNoteWindow? open))
        {
            open.ShowAndActivate();
            return;
        }
        StickyNoteWindow sticky = new(note, WindowPlacementService.FitOnScreen(bounds), draft, pinned);
        _stickies.Add(note.Id, sticky);
        sticky.Closed += (_, _) =>
        {
            _stickies.Remove(note.Id);
            if (!_exiting)
            {
                SaveStickyLayout();
                ReleaseIdle();
            }
        };
        if (activate)
            sticky.ShowAndActivate();
        else
            sticky.ShowInactive();
        SaveStickyLayout();
    }

    private void CreateSticky()
    {
        RectInt32 near = WindowPlacementService.NearTray(400, 480);
        int cascade = _stickies.Count % 8 * 32;
        OpenSticky(new Note { Paper = DefaultPaper }, new RectInt32(near.X - cascade, near.Y - cascade, near.Width, near.Height), draft: true);
    }

    private void ShowStickies()
    {
        foreach (StickyNoteWindow sticky in _stickies.Values)
            sticky.ShowAndActivate();
    }

    // Closing a sticky window puts its note back on the pad; this also shows it there.
    public async Task ReturnToStackAsync(Guid id)
    {
        if (_exitPending || !_stickies.TryGetValue(id, out StickyNoteWindow? sticky) || !await sticky.PrepareToCloseAsync())
            return;
        sticky.CloseWindow();
        await Notes.LoadAsync();
        if (Notes.Find(id) is Note note)
            await OpenNoteAsync(note);
        else
            await CreateNoteAsync();
    }

    public void SaveStickyLayout()
    {
        try
        {
            _layout.Save(_stickies.Values.Select(s => s.Placement));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            LogCrash(ex);
        }
    }

    // Sticky notes left on the desktop come back where they were.
    private async void RestoreStickies()
    {
        List<StickyPlacement> placements = _layout.Load();
        if (placements.Count == 0)
            return;
        try
        {
            await Notes.LoadAsync();
            foreach (StickyPlacement placement in placements)
            {
                // A sticky that was never written to has nothing to bring back.
                if (Notes.Find(placement.NoteId) is Note note)
                    OpenSticky(note, new RectInt32(placement.X, placement.Y, placement.Width, placement.Height),
                        pinned: placement.Pinned, activate: false);
            }
            SaveStickyLayout();
        }
        catch (Exception ex)
        {
            LogCrash(ex);
        }
    }

    private void ReleaseIdle()
    {
        if (_flyout is null && _stickies.Count == 0 && !_exiting)
        {
            Notes.Unload();
            MemoryService.ReleaseIdle(() => _flyout is null && _stickies.Count == 0);
        }
    }

    public async Task ExitAsync()
    {
        if (_exitPending)
            return;
        _exitPending = true;
        try
        {
            if (_flyout is not null)
            {
                if (!await _flyout.PrepareToCloseAsync())
                {
                    _flyout.ShowPopup();
                    return;
                }
            }
            foreach (StickyNoteWindow sticky in _stickies.Values.ToArray())
            {
                if (!await sticky.PrepareToCloseAsync())
                {
                    sticky.ShowAndActivate();
                    return;
                }
            }
            _exiting = true;
            // Sticky notes stay on the desktop for the next launch.
            SaveStickyLayout();
            foreach (StickyNoteWindow sticky in _stickies.Values.ToArray())
                sticky.CloseWindow();
            _flyout?.CloseWindow();
            SystemThemeService.Changed -= Theme_Changed;
            Notes.Changed -= Notes_Changed;
            SystemThemeService.Dispose();
            if (_tray is not null)
            {
                _tray.IsVisible = false;
                _tray.Dispose();
            }
            _showWait?.Unregister(null);
            _showEvent?.Dispose();
            _instance?.ReleaseMutex();
            _instance?.Dispose();
            Exit();
        }
        finally
        {
            _exitPending = false;
        }
    }

    private static void LogCrash(Exception? ex)
    {
        Debug.WriteLine($"MiniInk: {ex}");
        try
        {
            File.AppendAllText(Path.Combine(Path.GetTempPath(), "MiniInk-crash.log"), $"[{DateTime.Now:O}] {ex}\n");
        }
        catch (Exception logError) when (logError is IOException or UnauthorizedAccessException)
        {
            Debug.WriteLine($"MiniInk crash logging failed: {logError}");
        }
    }
}
