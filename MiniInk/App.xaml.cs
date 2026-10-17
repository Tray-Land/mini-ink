using System.Diagnostics;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
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
        if (ApplicationData.Current.LocalSettings.Values["HasLaunched"] is not true)
        {
            ApplicationData.Current.LocalSettings.Values["HasLaunched"] = true;
            ShowLibrary();
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
        MenuFlyoutItem open = new() { Text = "Open MiniInk", Icon = new SymbolIcon(Symbol.OpenPane) };
        open.Click += (_, _) => ShowPopup();
        MenuFlyoutItem exit = new() { Text = "Exit MiniInk", Icon = new SymbolIcon(Symbol.Cancel) };
        exit.Click += async (_, _) => await ExitAsync();
        menu.Items.Add(open);
        menu.Items.Add(create);
        menu.Items.Add(new MenuFlyoutSeparator());
        menu.Items.Add(exit);
        args.Flyout = menu;
    }

    public void ShowPopup()
    {
        if (!_exiting)
            EnsureFlyout().ShowPopup();
    }

    public async void ShowLibrary()
    {
        if (!_exiting)
            await EnsureFlyout().ShowLibraryAsync();
    }

    public async Task CreateNoteAsync()
    {
        if (_exitPending)
            return;
        try
        {
            await Notes.LoadAsync();
            Note note = new();
            await Notes.SaveAsync(note);
            await OpenNoteAsync(note);
        }
        catch (Exception ex)
        {
            LogCrash(ex);
            ShowLibrary();
            _flyout?.ShowError($"Could not create a note: {ex.Message}");
        }
    }

    public async Task OpenNoteAsync(Note note)
    {
        if (_exitPending)
            return;
        _tray?.CloseFlyout();
        await EnsureFlyout().OpenNoteAsync(note);
    }

    private void ReleaseIdle()
    {
        if (_flyout is null && !_exiting)
        {
            Notes.Unload();
            MemoryService.ReleaseIdle(() => _flyout is null);
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
            _exiting = true;
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
