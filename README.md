# MiniInk

Capture a thought, sketch with a pen, and find it later -- entirely in the tray.

![The compact MiniInk editor with typed text and selected ink](docs/tray-editor.png)

## A small canvas, one popup

MiniInk is a packaged WinUI 3 tray app using **Windows App SDK
2.5.4-experimental**, WinUIEx, and the `winui-tray-app` lifecycle pattern.
There is no main window and no separate note editor. Notes, search, and editing
all live in the same 440 by 620 logical-pixel, taskbar-anchored acrylic popup.

Left-click the notification-area icon to toggle the popup. Right-click for
Open MiniInk, New note, or Exit MiniInk. The back arrow returns from a note to
the library. The first launch opens the library; later launches reopen an
already-running instance's popup instead of adding another icon.

Dismissing the popup or pressing Escape saves the active note. Reopening
returns to it. After one minute hidden, the popup is closed and its UI,
subscriptions, and note cache are released. It is rebuilt from local storage
on the next click. Exit saves before removing the tray icon and releasing the
single-instance handles.

## Pen, text, and touch

- **Only pen input draws.** Choose a pen, pencil, highlighter, or eraser in the
  native ink toolbar. The initial pen color is blue for contrast in both themes.
- **Click empty space to type.** Click existing text to edit it, or use Add text
  in the toolbar. Text stays where it was placed.
- **Touch moves objects, not ink input.** Drag text with a finger. Tap ink to
  analyze it and select a recognized word, drawing, or individual unrecognized
  stroke; then drag the selected group with touch or mouse.
- The arrow keys nudge selected objects when a text field is not focused.
  Delete selected object is in the overflow menu, with confirmation.
- Autosave runs after a short idle interval, never during a pen stroke or
  object drag. Save and Ctrl+S also save immediately.

New notes use a 400 by 400 logical-pixel canvas. Older/larger notes retain their
full content with scrollbars. Touch remains reserved for object manipulation.
The popup follows the **taskbar's** light/dark mode, including theme-specific
tray icons generated from the supplied `icon.svg`.

## Local handwriting search

Title, typed text, recognized handwriting, and recognition alternatives are
indexed locally. Every whitespace-separated search term must match the note.
Original ink is preserved as ISF, not converted into text or replaced by
recognition results.

Windows handwriting language support must be installed for reliable
recognition. Accuracy varies with handwriting, language, and drawing style;
it is not guaranteed for every word. Ink is saved **before** analysis, so a
recognition failure cannot prevent note saving. Errors are shown in the
popup; Analyze ink retries.

Each note is an atomically replaced JSON file under the package's
`LocalState\Notes` folder, containing text positions, ISF ink encoded as
Base64, and handwriting search terms. Unreadable files are reported without
being overwritten. Crash diagnostics are written to `%TEMP%\MiniInk-crash.log`.
There are no accounts, API keys, network calls, or cloud uploads. Idle memory
is released after the UI closes; no network polling runs while hidden.

## Build and run

Requirements: Windows 10/11 with the supported Windows App SDK runtime,
.NET 10 SDK, Windows SDK, Developer Mode, and WinApp CLI 0.7+. The project
supports x64 and ARM64. This is an experimental SDK application; the inking
APIs may change or be removed in future releases.

```powershell
winget install Microsoft.WinAppCli
winapp run .\MiniInk\MiniInk.csproj --detach
```

No signed release installer is included yet. The CLI registers a development
package locally; it is not a Microsoft Store installation.

```powershell
# Pure search, storage, round-trip, and corruption checks
dotnet run --project .\MiniInk.Tests\MiniInk.Tests.csproj

# Local UI smoke checks (unlocked desktop; creates a uniquely named test note)
.\scripts\Test-Ui.ps1

# Regenerate the package assets and the two tray icons
.\scripts\Generate-Assets.ps1
```

UI tests create screenshots and a structured results report. Screen capture
is used because window-only capture does not faithfully render layered acrylic.
Test notes have titles beginning with `MiniInk smoke`.
