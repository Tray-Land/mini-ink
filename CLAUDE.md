## Tray app conventions

This is a tray-only WinUI 3 app following the `winui-tray-app` skill. Load that skill before changing app lifetime, the flyout, the tray icon, polling, settings, or secrets.

- Launch with `winapp run` (or `dotnet run`); never register the package by hand. Use `winapp run --clean` to test first-run behavior.
- `App.xaml.cs` owns the tray icon, single instance, and the only exit path (the Exit menu). There is no main window.
- The flyout (`TrayFlyoutWindow`) is hidden on dismiss and closed after a minute hidden, then `MemoryService.ReleaseIdle()` runs. Anything it starts, it must stop when hidden or closed.
- Network work runs only while visible, through a foreground poller. MiniInk currently makes no network calls.
- App API keys go in `OAuth.resw` (gitignored) and are read through a secrets service. User tokens go in PasswordVault. Nothing secret goes in settings.
- The repo is MIT licensed (`LICENSE`). Keep `README.md` in step with the features.
- Put pure decision logic (parsing, policies, formatting) in classes with no WinRT dependency, so the plain `net10.0` test project (`MiniInk.Tests`) can cover it.
- Releases: bump `Identity/@Version`, run `build-msix-for-gh.ps1`, publish the draft without changing its tag, then run `build-msix-for-gh.ps1 -VerifyPublished`. The script is a copy of the `winui-tray-app` skill's; fix it there and recopy, never here. Before a release, run the skill's audit.
