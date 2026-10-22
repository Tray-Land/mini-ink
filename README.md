# MiniInk

Capture a thought, sketch with a pen, and find it later -- entirely in the tray.

![The MiniInk popup on the newest note in the stack, with typed text and selected ink](docs/tray-editor.png)

## A pad of sticky notes in the tray

MiniInk is a packaged WinUI 3 tray app using **Windows App SDK
2.5.4-experimental**, WinUIEx, and the `winui-tray-app` lifecycle pattern.
There is no main window. The taskbar-anchored acrylic popup, 440 by 620
logical pixels until you resize it, is the pad: a blank page on top, and your
notes beneath it, newest first. Drag any edge or corner of the popup to resize
it (down to 360 by 400); it opens at that size from then on, and a popup too
big for the screen is held to what fits.

Left-click the notification-area icon to toggle the popup. Right-click for
Open MiniInk, New note, New sticky note, Show sticky notes (when any are on
the desktop), Settings, or Exit MiniInk. A second launch reopens the running
instance's popup instead of adding another icon. The first launch opens on a
blank page.

### Flipping through the stack

- **Older** and **Newer** (or Ctrl+Page Down and Ctrl+Page Up) flip through
  the pile. Newer from the newest note returns to the blank page. **New page** (Ctrl+N) jumps there.
- The pile's order is captured when the popup opens, so editing a note while
  flipping does not reshuffle it. The next time the popup opens, the notes you
  changed are on top. Only viewing a note does not move it.
- A blank page is not saved until something is written on it, so flipping and
  reopening never leave empty notes behind.
- **All notes** opens the searchable library. Picking a note there opens it in
  the stack. The bin on each note (or Delete on a focused note) deletes it
  for good after asking; a note on the desktop has its sticky window closed.

### Starting from a template

**New page** in the popup and **New note** in the library are split buttons.
The button itself (or Ctrl+N) opens a blank page; the arrow beside it starts
the page on printed paper instead:

- **Day by day**: the week in two columns, starting on Windows' first day of
  the week (or the one chosen in Settings), plus a Notes box. The page is
  titled for the week, such as `Week of October 18`.
- **Hour by hour**: one row per hour in your region's time format, titled with
  today's date. Settings picks either a fixed range (7 AM to 9 PM by default)
  or the next few hours, starting with the current one: six hours at 9:21 AM
  gives 9:00 AM through 2:00 PM, and a page made late at night runs past
  midnight. However many hours there are, the rows share the page's height
  between them. When the rows are tall enough, dotted dividers with small
  `:15`-style labels split each hour into the finest of 5, 10, 15, or 30
  minutes that still leaves about 20 pixels to write in.
- **To-do list**: rows, each with a box to tick.
- **Lined**, **Grid**, **Dot grid**, and **Graph** (squared paper with a
  bolder line every five squares and numbered axes; Settings chooses where the
  axes cross and how often they are numbered, or leaves them off).

The paper is printed behind the page rather than drawn on it, so the eraser and
selection never touch it, and it follows the light or dark theme. The library's
thumbnails show it in miniature behind the ink. The planners keep a fixed layout, so what is written in a day or an
hour stays there in a larger sticky window; the papers repeat to fill the page.
An hour planner fits its rows to the window it is in only while nothing is
written on it (tearing a blank one off fits it to the sticky window); after
that its rows stay put.
Like a blank page, a template page is kept only once something is written on it
(its suggested title doesn't count), and tearing one off keeps its paper.

Each page keeps the planner and graph choices it was made with, so changing
them in Settings never moves the lines under existing writing. A blank planner
or graph page that is still open when you come back from Settings is redrawn
with the new choices, and a blank next-few-hours page moves on to the current
hour when the popup is opened again.

### Settings

The gear beside **New note** in the library, **Settings** in the editor's
**...** menu, and **Settings** in the tray menu all open settings in the popup.
The back button, Alt+Left, or Escape returns to the library or note it was
opened over; changes apply immediately.

- **Start with Windows** (off by default). If it was turned off in Windows
  Settings > Apps > Startup or by policy, the page says so instead of offering
  a toggle that can't change it.
- **Open to a fresh page** (see below).
- **New notes start on** and **New notes use the color**: the paper and the
  color of every new blank page: the one the popup opens to, the one above the
  newest note, the one left behind by Tear off, a new sticky note, and the
  one New page or Ctrl+N makes.
- **Popup**: the width and height it opens at, 360 to 2000 by 400 to 2000
  (440 by 620 by default). They follow the popup's edges when you drag them,
  and change the open popup at once when you set them here; **Reset** goes back
  to the default.
- **Pens**: the pens on every toolbar. **Add pen** puts a pen, pencil, or
  highlighter on the end (up to five); the bin beside a pen removes it (there is
  always at least one); drag a pen by its handle to put it in another place
  (the toolbars follow the same order); **Reset pens** goes back to a pen, a
  pencil, and a highlighter in their starting colors and sizes.
- **Planners**: whether Hour by hour pages show a fixed range (and which
  hours) or the next 1 to 24 hours, and the first day of the week for Day by
  day (or the same as Windows).
- **Graph**: where the axes cross (center, bottom left, middle left, or no
  axes) and how often they are numbered (every 5 or 10 squares, every square,
  or not at all).

### A fresh page after a while away

Pressing Escape or clicking away puts the popup down and saves the note.
Picking it up again quickly returns to exactly what you were doing. After it
has been put down for longer than **Open to a fresh page** (in Settings: 1,
5 (default), 15 or 60 minutes, or Never), it opens on a blank
page instead; the previous note is waiting one step down the pile. Never
always reopens the last note.

After one minute hidden, the popup is closed and its UI, subscriptions, and
note cache are released. It is rebuilt from local storage on the next click.
Exit saves before removing the tray icon and releasing the single-instance
handles.

### Tearing off sticky notes

**Tear off** (the window button beside New page) lifts the current note off
the pad into its own window, right where the popup was, and leaves a blank page
behind. Sticky windows:

- are just the page: one slim strip holds your pens and an eraser, Mouse and
  touch, the pin, Put back in the stack, and close (the window is kept wide
  enough for however many pens there are). There is no title box or
  toolbar; the note autosaves as you write;
- resize freely, and their canvas grows to fill them;
- stay above other windows when **Keep on top** (the pin) is on;
- leave the stack while they are on the desktop. The library marks them
  "on your desktop", and picking one there brings its window forward;
- go back into the stack when closed. **Put back in the stack** also closes
  the window and opens the note in the popup;
- come back where they were, pinned or not, the next time MiniInk starts,
  without taking focus. Positions are pulled back on screen if a monitor is
  gone. A sticky that was never written on is not brought back.

New sticky note in the tray menu opens a blank sticky window directly.

![A torn-off sticky note window, kept on top](docs/sticky-note.png)

## Pen, mouse, and touch

- **The pen always draws.** Choose a pen, pencil, highlighter, or eraser in the
  native ink toolbar (the full toolbar also has a ruler; sticky notes leave it
  off). The pens are the ones set in Settings, so you can keep, say, a red pen
  and a thick highlighter; pens of a kind look and draw alike (same icon,
  colors, and widths), each tinted with its own ink color. Right-click a pen
  (or press and hold) and choose **Move left** or **Move right** to put it in
  another place; Settings has the same order and a handle to drag pens by. The
  canvas always draws in the color the toolbar shows.
- **Pens are remembered.** The color and size each pen was left at, and the pen
  in hand, are kept for every toolbar and the next session, so MiniInk opens
  with the pens set up the way you left them. A pen you have not given a color
  starts white on a dark page and black on a light (or sticky colored) one, and
  follows the page if it changes; a color you pick stays.
- **Mouse and touch** (in the editor's overflow menu, or the arrow-and-hand
  button beside a sticky note's ink tools) sets what the mouse does and what
  touch does, each one of:
  - **Add text** (the mouse's default): click or tap empty space to type
    there, or existing text to edit it.
  - **Select and drag** (touch's default): tap ink to select it together with
    every stroke touching it, or within a few pixels of one that does, which
    picks up a word or a drawing. Tap text to select it, and tap it again to
    edit it.
  - **Draw**: ink like the pen, with whichever ink tool is chosen.

  With Add text or Select and drag, dragging text or a selected ink group
  moves it; text stays where it is placed. The choice applies to the popup and
  every sticky note and is remembered. Add text in the toolbar also starts a
  text box.
- The arrow keys nudge selected objects when a text field is not focused.
  Escape clears a selection first, then puts the popup down.
  Delete (or Delete selected object in the overflow menu) removes the
  selection, with confirmation.
- Autosave runs after a short idle interval, never during a pen stroke or
  object drag. Ctrl+S (or Save in the overflow menu) also saves immediately.

The canvas fills the popup or sticky window, with a 400 by 400 logical-pixel
minimum. It grows to keep anything placed further out, such as ink drawn in a
large sticky window, so smaller windows show that content with scrollbars.
Scrolling never takes over touch; it stays with the chosen touch tool.
The popup follows the **taskbar's** light/dark mode, including theme-specific
tray icons generated from the supplied `icon.svg`.

## Finding notes

The library lists notes newest first, each with a thumbnail of its page, its
title, any typed text, and when it was last changed. A thumbnail shows the
page's paper color and template with the ink on top, so notes are told apart
at a glance. Handwriting is not recognized, so ink is found by eye from its
thumbnail. Search matches the
title, typed text, and date, either as the list shows it (`Oct 19`) or
written out (`October`, `Monday`, `2026`); every whitespace-separated term
must match.

Ink thumbnails are small line drawings of the ink, fitted to the tile and never
enlarged, drawn on a worker thread when a note is saved and kept with it. Each
stroke keeps the color it was drawn in, with highlighters drawn broad and
translucent under the pens; black and white ink, which is what a pen writes in
until a color is picked, is drawn in the page's ink instead, so it stays
readable when the theme changes. Notes saved before thumbnails existed, or
before they kept colors, get one drawn from their ink the first time the
library shows them. The paper behind the ink is not saved: the tile
draws it from the note's template and color each time the library opens, so it
is a sketch of the paper rather than the page's exact layout, and a colored
page's ink and rules go dark as they do in the editor.

Each note is an atomically replaced JSON file under the package's
`LocalState\Notes` folder, containing text positions, ISF ink encoded as
Base64, its thumbnail, and the template it was started from. Unreadable files are reported without
being overwritten. Sticky window positions, sizes, and pins are kept in
`LocalState\StickyNotes.json`; if that file is unreadable, MiniInk starts with
no sticky windows. Crash diagnostics are written to `%TEMP%\MiniInk-crash.log`.
There are no accounts, API keys, network calls, or cloud uploads. Idle memory
is released once the popup and every sticky window have closed; no network
polling runs while hidden.

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
# Pure search, thumbnails, stack order, fresh-page timing, templates, sticky layout, storage, and corruption checks
dotnet run --project .\MiniInk.Tests\MiniInk.Tests.csproj

# Local UI smoke checks (unlocked desktop; creates a uniquely named test note)
.\scripts\Test-Ui.ps1

# Regenerate the package assets and the two tray icons
.\scripts\Generate-Assets.ps1
```

UI tests create screenshots and a structured results report. Screen capture
is used because window-only capture does not faithfully render layered acrylic.
Test notes have titles beginning with `MiniInk smoke`.
