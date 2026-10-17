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
function Saved {
    Ui @('wait-for', 'NoteStatus', '--value', 'Saved on this device', '--contains', '-t', '15000') | Out-Null
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
    $family = (Get-AppxPackage -Name '79F360F7-8614-43F8-BA5C-65F81BCEA4E2').PackageFamilyName
    if (-not $family) { throw 'MiniInk package identity not found.' }
    $noteFolder = Join-Path $env:LOCALAPPDATA "Packages\$family\LocalState\Notes"
    if (Element 'BackToNotes') { Ui @('invoke', 'BackToNotes') | Out-Null }
    Ui @('wait-for', 'NewNote', '-t', '10000') | Out-Null
    Test 'Library is entirely in one tray popup' {
        if (@($windows | Where-Object { $_.title -ne 'PopupHost' }).Count -ne 1) { throw 'An additional app window is open.' }
        Capture '01-library.png'
    }
    Test 'New note stays in the same window' {
        Ui @('invoke', 'NewNote')
        Ui @('wait-for', 'NoteTitle', '-t', '10000')
        Ui @('set-value', 'NoteTitle', $marker)
        $windows = @(Checked @('ui', 'list-windows', '-a', "$AppPid", '--json') | ConvertFrom-Json)
        if (@($windows | Where-Object { $_.title -ne 'PopupHost' }).Count -ne 1) { throw 'New note opened a separate window.' }
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
        Ui @('invoke', 'SaveNote')
        Saved
        $before = Note
        if ($before.Texts.Count -ne 1) { throw 'Mouse click did not create one text object.' }
        Ui @('wait-for', 'NoteStatus', '--value', '0 ink strokes', '--contains', '-t', '3000')
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
        Start-Sleep -Seconds 2
        Saved
        $after = Note
        if ([double]$after.Texts[0].Y - [double]$before.Texts[0].Y -lt 20) { throw 'Text did not move at least 20 canvas pixels.' }
        Ui @('touch', '--at', (Point 310 300), '--to-point', (Point 350 330), '-g', 'swipe')
        Ui @('wait-for', 'NoteStatus', '--value', '0 ink strokes', '--contains', '-t', '3000')
    }
    Test 'Pen creates ink and local handwriting index' {
        $path = @((Point 55 240), (Point 85 260), (Point 115 240), (Point 145 260), (Point 175 240)) -join ' '
        Ui @('pen', '--path', $path, '--duration-ms', '500')
        Start-Sleep -Seconds 3
        Saved
        Ui @('wait-for', 'NoteStatus', '--value', '1 ink strokes', '--contains', '-t', '3000')
        $note = Note
        if ($note.InkBase64.Length -eq 0) { throw 'Ink was not persisted.' }
        if ($note.IndexError) { throw "Handwriting indexing failed: $($note.IndexError)" }
        if (-not ($note.RecognizedText + $note.HandwritingKeywords).Trim()) { throw 'No handwriting search terms were produced.' }
    }
    Test 'Tap analyzes/selects ink; touch drags the selected group' {
        Ui @('touch', '--at', (Point 85 260))
        Ui @('wait-for', 'NoteStatus', '--value', 'Ink group selected', '--contains', '-t', '10000')
        $before = Note
        Ui @('touch', '--at', (Point 85 260), '--to-point', (Point 125 310), '-g', 'swipe')
        Start-Sleep -Seconds 3
        Saved
        $after = Note
        if ($before.InkBase64 -eq $after.InkBase64) { throw 'Moving selected ink did not change the persisted ink.' }
        Capture '02-editor.png'
    }
    Test 'Dismiss saves and second launch restores the same note' {
        Ui @('send-keys', 'escape', '--via', 'send-input')
        Start-Sleep -Seconds 2
        Checked @('run', $project, '--no-build', '--detach', '--json') | Out-Null
        Ui @('wait-for', 'NoteTitle', '--value', $marker, '-t', '10000')
        if ((Note).Texts[0].Text -ne 'local search marker') { throw 'Text did not survive dismiss.' }
    }
    Test 'Handwriting-only search finds the saved note' {
        $note = Note
        $term = @($note.RecognizedText -split '\s+' | Where-Object { $_ })[0]
        if (-not $term) { $term = @($note.HandwritingKeywords -split '\s+' | Where-Object { $_ })[0] }
        Ui @('invoke', 'BackToNotes')
        Ui @('wait-for', 'TextBox', '-t', '10000')
        Ui @('set-value', 'TextBox', $term)
        Ui @('wait-for', $marker, '-t', '5000')
        Capture '03-search.png'
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
