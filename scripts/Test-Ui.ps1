param(
    [int]$AppPid = 0,
    [string]$ArtifactDirectory = (Join-Path $PSScriptRoot "..\ui-test-artifacts\$([guid]::NewGuid().ToString('N'))")
)
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false
$previousWorkflow = $env:WINAPP_UI_WORKFLOW_ID
$env:WINAPP_UI_WORKFLOW_ID = [guid]::NewGuid().ToString('N')
$results = [Collections.Generic.List[object]]::new()
$screenshots = [Collections.Generic.List[string]]::new()
$project = Join-Path $PSScriptRoot '..\MiniInk\MiniInk.csproj'
$hwnd = $null
$marker = "MiniInk smoke $([guid]::NewGuid().ToString('N').Substring(0,8))"

function Checked([string[]]$Arguments) {
    $output = @(& winapp @Arguments)
    if ($LASTEXITCODE -ne 0) { throw "winapp failed: $($Arguments -join ' ')`n$($output -join "`n")" }
    $output
}
function Ui([string[]]$Arguments) { Checked (@('ui') + $Arguments + @('-w', "$hwnd")) }
function Test([string]$Name, [scriptblock]$Action) {
    try { & $Action | Out-Null; $results.Add(@{ name=$Name; status='PASS' }) }
    catch { $results.Add(@{ name=$Name; status='FAIL'; detail="$_" }); throw }
}
function Nodes($items) {
    foreach ($item in $items) { $item; Nodes $item.children }
}
function Inspect { Ui @('inspect', '--json', '--depth', '8') | ConvertFrom-Json }
function Element([string]$Id) {
    $inspection = Inspect
    Nodes @($inspection.windows | ForEach-Object { $_.elements }) |
        Where-Object { $_.automationId -eq $Id -and -not $_.isOffscreen } | Select-Object -First 1
}
function Capture([string]$Name) {
    $path = Join-Path $ArtifactDirectory $Name
    Ui @('screenshot', '--capture-screen', '--output', $path) | Out-Null
    if (-not (Test-Path $path) -or (Get-Item $path).Length -eq 0) { throw 'Screenshot missing.' }
    $screenshots.Add($path)
}
# The page shows no save status, so wait for the saved smoke note to pass $Condition (given the note as $args[0]).
function Saved([scriptblock]$Condition, [string]$Failure) {
    $deadline = [DateTime]::UtcNow.AddSeconds(15)
    do {
        Start-Sleep -Milliseconds 250
        $saved = try { Note } catch { $null }
        if ($saved -and (& $Condition $saved)) { return $saved }
    } while ([DateTime]::UtcNow -lt $deadline)
    throw $Failure
}
function Note {
    $matches = @(Get-ChildItem -LiteralPath $noteFolder -Filter '*.json' | ForEach-Object {
        $note = Get-Content -LiteralPath $_.FullName -Raw | ConvertFrom-Json
        if ($note.Title -eq $marker) { $note }
    })
    if ($matches.Count -ne 1) { throw "Expected one persisted smoke note; got $($matches.Count)." }
    $matches[0]
}

try {
    $ArtifactDirectory = [IO.Path]::GetFullPath($ArtifactDirectory)
    New-Item -ItemType Directory -Force $ArtifactDirectory | Out-Null
    if (-not $AppPid) {
        $launch = Checked @('run', $project, '--detach', '--json') | ConvertFrom-Json
        $AppPid = [int]$launch.ProcessId
    }
    Checked @('run', $project, '--no-build', '--detach', '--json') | Out-Null
    $deadline = [DateTime]::UtcNow.AddSeconds(10)
    do {
        Start-Sleep -Milliseconds 500
        $windows = @(Checked @('ui', 'list-windows', '-a', "$AppPid", '--json') | ConvertFrom-Json)
        $main = $windows | Where-Object { $_.title -eq 'MiniInk' } | Select-Object -First 1
    } while (-not $main -and [DateTime]::UtcNow -lt $deadline)
    if (-not $main) { throw 'The tray popup did not appear.' }
    $hwnd = "$($main.hwnd)"
    # Sticky notes already on the desktop are separate windows by design.
    $stickies = @($windows | Where-Object { $_.title -notin 'PopupHost', 'MiniInk' }).Count
    $family = (Get-AppxPackage -Name '79F360F7-8614-43F8-BA5C-65F81BCEA4E2').PackageFamilyName
    if (-not $family) { throw 'MiniInk package identity not found.' }
    $noteFolder = Join-Path $env:LOCALAPPDATA "Packages\$family\LocalState\Notes"
    if (Element 'BackToNotes') { Ui @('invoke', 'BackToNotes') | Out-Null }
    Ui @('wait-for', 'NewNote', '-t', '10000') | Out-Null
    Test 'Library is entirely in one tray popup' {
        if (@($windows | Where-Object { $_.title -ne 'PopupHost' }).Count -ne (1 + $stickies)) { throw 'An additional app window is open.' }
        Capture '01-library.png'
    }
    Test 'New note stays in the same window' {
        Ui @('invoke', 'NewNote')
        Ui @('wait-for', 'NoteTitle', '-t', '10000')
        Ui @('set-value', 'NoteTitle', $marker)
        $windows = @(Checked @('ui', 'list-windows', '-a', "$AppPid", '--json') | ConvertFrom-Json)
        if (@($windows | Where-Object { $_.title -ne 'PopupHost' }).Count -ne (1 + $stickies)) { throw 'New note opened a separate window.' }
    }
    $canvas = Element 'NoteCanvas'
    if (-not $canvas -or $canvas.width -le 0) { throw 'Canvas bounds unavailable.' }
    $inspection = Inspect
    $scale = [double]$inspection.windows[0].scale
    function Point([double]$X, [double]$Y) { "$([int]($canvas.x + $X*$scale)),$([int]($canvas.y + $Y*$scale))" }
    Test 'Mouse click adds text, not ink' {
        $point = Point 30 30
        Ui @('drag', $point, $point)
        Ui @('wait-for', 'CanvasText', '-t', '3000')
        Ui @('set-value', 'CanvasText', 'local search marker')
        Ui @('invoke', 'MoreButton')
        Ui @('wait-for', 'SaveNote', '-t', '3000')
        Ui @('invoke', 'SaveNote')
        $saved = Saved { $args[0].Texts.Count -eq 1 } 'Mouse click did not create one text object.'
        if ($saved.StrokeCount -ne 0) { throw 'Mouse click drew ink.' }
    }
    Test 'Touch moves text without drawing' {
        $before = Note
        $inspection = Inspect
        $text = Nodes @($inspection.windows | ForEach-Object { $_.elements }) |
            Where-Object { $_.type -eq 'Text' -and $_.name -eq 'local search marker' -and -not $_.isOffscreen } |
            Select-Object -First 1
        if (-not $text) { throw 'Text object bounds unavailable.' }
        $x = [int]($text.x + 15*$scale)
        $y = [int]($text.y + 10*$scale)
        Ui @('touch', '--at', "$x,$y", '--to-point', "$x,$([int]($y+65*$scale))", '-g', 'swipe')
        Saved { [double]$args[0].Texts[0].Y - [double]$before.Texts[0].Y -ge 20 } 'Text did not move at least 20 canvas pixels.' | Out-Null
        Ui @('touch', '--at', (Point 310 300), '--to-point', (Point 350 330), '-g', 'swipe')
        # Ink would be autosaved within a couple of seconds.
        Start-Sleep -Seconds 3
        if ((Note).StrokeCount -ne 0) { throw 'Touch drew ink.' }
    }
    Test 'Pen creates ink and its thumbnail' {
        $path = @((Point 55 240), (Point 85 260), (Point 115 240), (Point 145 260), (Point 175 240)) -join ' '
        Ui @('pen', '--path', $path, '--duration-ms', '500')
        $note = Saved { $args[0].StrokeCount -eq 1 } 'The pen stroke was not saved.'
        if ($note.InkBase64.Length -eq 0) { throw 'Ink was not persisted.' }
        if ($note.Thumbnail -notmatch '^p[0-9A-F]{8}\|M[\d.]+,[\d.]+L') { throw "No thumbnail was drawn: '$($note.Thumbnail)'" }
    }
    Test 'Tap selects ink; touch drags the selected group' {
        Ui @('touch', '--at', (Point 85 260))
        Start-Sleep -Milliseconds 500
        $before = Note
        Ui @('touch', '--at', (Point 85 260), '--to-point', (Point 125 310), '-g', 'swipe')
        Saved { $args[0].InkBase64 -ne $before.InkBase64 } 'Moving selected ink did not change the persisted ink.' | Out-Null
        Capture '02-editor.png'
    }
    Test 'Dismiss saves and second launch restores the same note' {
        Ui @('send-keys', 'escape', '--via', 'send-input')
        Start-Sleep -Seconds 2
        Checked @('run', $project, '--no-build', '--detach', '--json') | Out-Null
        Ui @('wait-for', 'NoteTitle', '--value', $marker, '-t', '10000')
        if ((Note).Texts[0].Text -ne 'local search marker') { throw 'Text did not survive dismiss.' }
    }
    Test 'Search finds the saved note by typed text and date' {
        # Only the date written out holds the year.
        $year = ([datetime](Note).Updated).ToLocalTime().Year
        Ui @('invoke', 'BackToNotes')
        Ui @('wait-for', 'TextBox', '-t', '10000')
        Ui @('set-value', 'TextBox', "marker $year")
        Ui @('wait-for', $marker, '-t', '5000')
        Capture '03-search.png'
    }
    Test 'Stack flips to the blank page and back' {
        Ui @('click', $marker)
        Ui @('wait-for', 'NoteTitle', '--value', $marker, '-t', '10000')
        Ui @('invoke', 'NewerNote')
        # Only the blank page above the newest note has nothing newer.
        Ui @('wait-for', 'NewerNote', '--property', 'IsEnabled', '--value', 'False', '-t', '5000')
        Ui @('invoke', 'OlderNote')
        Ui @('wait-for', 'NoteTitle', '--value', $marker, '-t', '5000')
    }
    Test 'Tear-off opens a plain sticky window that pins and goes back' {
        $id = (Note).Id
        $layoutPath = Join-Path $noteFolder '..\StickyNotes.json'
        function Placement { @(Get-Content -LiteralPath $layoutPath -Raw | ConvertFrom-Json) | Where-Object NoteId -eq $id }
        Ui @('invoke', 'TearOff')
        $deadline = [DateTime]::UtcNow.AddSeconds(10)
        do {
            Start-Sleep -Milliseconds 500
            $sticky = @(Checked @('ui', 'list-windows', '-a', "$AppPid", '--json') | ConvertFrom-Json) |
                Where-Object { $_.title -eq $marker } | Select-Object -First 1
        } while (-not $sticky -and [DateTime]::UtcNow -lt $deadline)
        if (-not $sticky) { throw 'Tear-off did not open a sticky window.' }
        if (-not (Placement)) { throw 'The sticky window was not saved for the next launch.' }
        $shown = @(Nodes @((Checked @('ui', 'inspect', '--json', '--depth', '8', '-w', "$($sticky.hwnd)") | ConvertFrom-Json).windows |
            ForEach-Object { $_.elements }) | Where-Object { -not $_.isOffscreen } | ForEach-Object { $_.automationId })
        if ('PenTools' -notin $shown -or 'NoteCanvas' -notin $shown) { throw 'The sticky window lost its pen tools or canvas.' }
        if (@('NoteTitle', 'SaveNote', 'NewPage' | Where-Object { $_ -in $shown }).Count) { throw 'The sticky window shows editor chrome.' }
        Checked @('ui', 'invoke', 'PinNote', '-w', "$($sticky.hwnd)") | Out-Null
        Start-Sleep -Seconds 1
        if (-not (Placement).Pinned) { throw 'Pinning was not saved.' }
        $path = Join-Path $ArtifactDirectory '04-sticky.png'
        Checked @('ui', 'screenshot', '--capture-screen', '--output', $path, '-w', "$($sticky.hwnd)") | Out-Null
        $screenshots.Add($path)
        Checked @('ui', 'invoke', 'ReturnToStack', '-w', "$($sticky.hwnd)") | Out-Null
        Ui @('wait-for', 'NoteTitle', '--value', $marker, '-t', '10000')
        if (Placement) { throw 'The note stayed on the desktop after being put back.' }
    }
    Test 'The New page arrow starts an unsaved page from a template' {
        $before = @(Get-ChildItem -LiteralPath $noteFolder -Filter '*.json').Count
        Ui @('invoke', 'NewPage', '--action', 'expand')
        Ui @('wait-for', 'TemplateWeek', '-t', '5000')
        Ui @('invoke', 'TemplateWeek')
        Ui @('wait-for', 'NoteTitle', '--value', 'Week of', '--contains', '-t', '5000')
        Ui @('wait-for', 'NewerNote', '--property', 'IsEnabled', '--value', 'False', '-t', '5000')
        Capture '05-template.png'
        Ui @('invoke', 'NewPage')
        $deadline = [DateTime]::UtcNow.AddSeconds(5)
        do {
            Start-Sleep -Milliseconds 200
            $title = "$(Ui @('get-value', 'NoteTitle') | Select-Object -Last 1)".Trim()
        } while ($title -and [DateTime]::UtcNow -lt $deadline)
        if ($title) { throw 'New page kept the template page instead of opening a blank one.' }
        if (@(Get-ChildItem -LiteralPath $noteFolder -Filter '*.json').Count -ne $before) { throw 'An unwritten template page was saved.' }
    }
    Test 'The library deletes a note after asking' {
        Ui @('invoke', 'BackToNotes')
        Ui @('wait-for', 'TextBox', '-t', '10000')
        Ui @('set-value', 'TextBox', $marker)
        Ui @('wait-for', $marker, '-t', '5000')
        Ui @('invoke', 'DeleteNote')
        Ui @('wait-for', 'PrimaryButton', '-t', '5000')
        Capture '06-delete.png'
        Ui @('invoke', 'PrimaryButton')
        Ui @('wait-for', $marker, '--gone', '-t', '5000')
        $left = @(Get-ChildItem -LiteralPath $noteFolder -Filter '*.json' |
            Where-Object { (Get-Content -LiteralPath $_.FullName -Raw | ConvertFrom-Json).Title -eq $marker })
        if ($left.Count) { throw 'The deleted note is still saved.' }
    }
    Test 'Settings open over the library and Escape goes back to it' {
        Ui @('invoke', 'OpenSettings')
        Ui @('wait-for', 'SettingsBack', '-t', '5000')
        $shown = @(Nodes @((Inspect).windows | ForEach-Object { $_.elements }) | ForEach-Object { $_.automationId })
        $missing = @('StartWithWindows', 'FreshAfter', 'HourRange', 'DayStartsAt', 'DayEndsAt', 'WeekStartsOn', 'GraphAxes', 'GraphNumbers', 'PopupWidth', 'PopupHeight', 'ResetPopupSize', 'AddPen', 'ResetPens', 'RemovePen0' |
            Where-Object { $_ -notin $shown })
        if ($missing.Count) { throw "Settings is missing $($missing -join ', ')." }
        Capture '07-settings.png'
        Ui @('send-keys', 'escape', '--via', 'send-input')
        Ui @('wait-for', 'NewNote', '-t', '5000')
        if (-not (Element 'NewNote')) { throw 'Escape put the popup down instead of leaving settings.' }
    }
    Test 'A pen is added and removed in Settings' {
        function PenRows { @(Nodes @((Inspect).windows | ForEach-Object { $_.elements }) | Where-Object { $_.automationId -like 'RemovePen*' }) }
        Ui @('invoke', 'OpenSettings')
        Ui @('wait-for', 'AddPen', '-t', '5000')
        $rows = PenRows
        if ($rows.Count -ge 5) { throw 'The test needs room to add a pen.' }
        # A new pen goes on the end, whatever the pens set up before.
        Ui @('invoke', 'AddPen')
        Ui @('wait-for', 'AddPenBallpoint', '-t', '4000')
        Ui @('invoke', 'AddPenBallpoint')
        Ui @('wait-for', "RemovePen$($rows.Count)", '-t', '4000')
        Capture '08-pens.png'
        Ui @('invoke', "RemovePen$($rows.Count)")
        Ui @('wait-for', "RemovePen$($rows.Count)", '--gone', '-t', '4000')
        if ((PenRows).Count -ne $rows.Count) { throw 'Removing the added pen did not put the pens back.' }
        Ui @('send-keys', 'escape', '--via', 'send-input')
        Ui @('wait-for', 'NewNote', '-t', '5000')
    }
    Test 'Pens are put in order by dragging in Settings and from the toolbar menu' {
        function PenNames { @(Nodes @((Inspect).windows | ForEach-Object { $_.elements }) | Where-Object { $_.automationId -like 'RemovePen*' } |
            Sort-Object automationId | ForEach-Object { $_.name -replace '^Remove ', '' }) }
        function Kind([string]$Name) { $Name -replace ' \d+$', '' }
        Ui @('invoke', 'OpenSettings')
        Ui @('wait-for', 'AddPen', '-t', '5000')
        $start = PenNames
        if ($start.Count -ge 5) { throw 'The test needs room to add a pen.' }
        # A pen of a kind the first one is not, so that putting it first shows in the names. A pen of a kind is
        # named for it ("Pencil"), and numbered only when it is not the first of its kind.
        $kind = if ((Kind $start[0]) -eq 'Highlighter') { 'Pencil' } else { 'Highlighter' }
        $added = $start.Count
        Ui @('invoke', 'AddPen')
        Ui @('wait-for', "AddPen$kind", '-t', '4000')
        Ui @('invoke', "AddPen$kind")
        Ui @('wait-for', "RemovePen$added", '-t', '4000')
        # Where the added pen is, as far as the test has seen, so that it can be the one taken away.
        $position = $added
        try {
            Ui @('drag', "MovePen$added", 'MovePen0')
            Ui @('wait-for', 'RemovePen0', '--property', 'Name', '--value', "Remove $kind", '-t', '4000')
            $position = 0
            $names = PenNames
            if ($names.Count -ne $added + 1 -or (Kind $names[0]) -ne $kind) { throw "Dragging the pen to the top left the pens as: $($names -join ', ')." }
            Capture '09-pen-order.png'
            # Settings were opened over the library, so the toolbar is in a blank page opened from it.
            Ui @('send-keys', 'escape', '--via', 'send-input')
            Ui @('wait-for', 'NewNote', '-t', '5000')
            Ui @('invoke', 'NewNote')
            Ui @('wait-for', 'Pen0', '--property', 'Name', '--value', $names[0], '-t', '10000')
            Ui @('click', 'Pen0', '--right')
            Ui @('wait-for', 'MovePenRight', '-t', '4000')
            Ui @('wait-for', 'MovePenLeft', '--property', 'IsEnabled', '--value', 'False', '-t', '4000')
            Ui @('invoke', 'MovePenRight')
            Ui @('wait-for', 'Pen1', '--property', 'Name', '--value', $names[0], '-t', '4000')
            Ui @('wait-for', 'Pen0', '--property', 'Name', '--value', $names[1], '-t', '4000')
            $position = 1
            Capture '10-pen-menu.png'
        }
        finally {
            # Whatever came of it, the pen added here goes, leaving the others as they were. This is only
            # tidying up, so it does not hide why the test failed.
            try {
                if (-not (Element 'AddPen')) {
                    if (Element 'MoreButton') {
                        Ui @('invoke', 'MoreButton')
                        Ui @('wait-for', 'OpenSettings', '-t', '4000')
                        Ui @('invoke', 'OpenSettings')
                    }
                    Ui @('wait-for', 'AddPen', '-t', '5000')
                }
                if ((PenNames).Count -eq $added + 1) { Ui @('invoke', "RemovePen$position") }
            }
            catch { }
        }
        Ui @('wait-for', "RemovePen$added", '--gone', '-t', '4000')
        $restored = (PenNames) -join ', '
        if ($restored -ne ($start -join ', ')) { throw "Taking the added pen away left the pens as: $restored." }
        Ui @('send-keys', 'escape', '--via', 'send-input')
        Ui @('wait-for', 'NoteTitle', '-t', '5000')
        Ui @('invoke', 'BackToNotes')
        Ui @('wait-for', 'NewNote', '-t', '5000')
    }
    Test 'The popup is resized in Settings and by dragging its edge' {
        function PopupPixels {
            $window = @(Checked @('ui', 'list-windows', '-a', "$AppPid", '--json') | ConvertFrom-Json) | Where-Object { "$($_.hwnd)" -eq $hwnd }
            "$($window.width)x$($window.height)"
        }
        function WaitForSize([int]$Width, [int]$Height) {
            $expected = "$([Math]::Ceiling($Width * $scale))x$([Math]::Ceiling($Height * $scale))"
            $deadline = [DateTime]::UtcNow.AddSeconds(5)
            do { Start-Sleep -Milliseconds 250; $now = PopupPixels } while ($now -ne $expected -and [DateTime]::UtcNow -lt $deadline)
            if ($now -ne $expected) { throw "The popup is $now pixels, not $expected." }
        }
        function Box([string]$Id) { (Nodes @((Element $Id).children) | Where-Object { $_.automationId -eq 'InputBox' }).value }
        Ui @('invoke', 'OpenSettings')
        Ui @('wait-for', 'PopupWidth', '-t', '5000')
        # The size to leave the popup at, which is the one it had.
        $opened = (PopupPixels) -split 'x'
        $width = [int][Math]::Round([int]$opened[0] / $scale)
        $height = [int][Math]::Round([int]$opened[1] / $scale)
        try {
            Ui @('set-value', 'PopupWidth', '520')
            Ui @('set-value', 'PopupHeight', '500')
            WaitForSize 520 500
            Ui @('invoke', 'ResetPopupSize')
            WaitForSize 440 620
            if ((Box 'PopupWidth') -ne '440' -or (Box 'PopupHeight') -ne '620') { throw 'Reset left the boxes at another size.' }
            # The left edge is dragged in, so the popup is narrower and its right edge stays where it was.
            $window = (Inspect).windows[0].elements[0]
            $x = [int]$window.x + 2
            $y = [int]($window.y + $window.height / 2)
            Ui @('drag', "$x,$y", "$([int]($x + 40 * $scale)),$y")
            WaitForSize 400 620
            $deadline = [DateTime]::UtcNow.AddSeconds(5)
            while ((Box 'PopupWidth') -ne '400' -and [DateTime]::UtcNow -lt $deadline) { Start-Sleep -Milliseconds 250 }
            if ((Box 'PopupWidth') -ne '400') { throw "Dragging the popup's edge left the width box at $(Box 'PopupWidth'), not 400." }
            Capture '11-popup-size.png'
        }
        finally {
            # Tidying up, so it does not hide why the test failed.
            try {
                Ui @('set-value', 'PopupWidth', "$width")
                Ui @('set-value', 'PopupHeight', "$height")
            }
            catch { }
        }
        WaitForSize $width $height
        Ui @('send-keys', 'escape', '--via', 'send-input')
        Ui @('wait-for', 'NewNote', '-t', '5000')
    }
} catch {
    $results.Add(@{ name='UI execution'; status='FAIL'; detail="$_" })
} finally {
    try { Checked @('ui', 'yield') | Out-Null } finally { $env:WINAPP_UI_WORKFLOW_ID = $previousWorkflow }
}
$failed = @($results | Where-Object status -eq 'FAIL').Count
@{ appPid=$AppPid; marker=$marker; passed=$results.Count-$failed; failed=$failed;
    results=@($results.ToArray()); screenshots=@($screenshots.ToArray()) } |
    ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $ArtifactDirectory 'results.json')
Write-Host "Passed: $($results.Count-$failed); failed: $failed. Artifacts: $ArtifactDirectory"
if ($failed) { exit 1 }
