param([string]$Source = (Join-Path $PSScriptRoot '..\branding\icon.svg'))
$ErrorActionPreference = 'Stop'
$project = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\MiniInk'))
$branding = Join-Path $PSScriptRoot '..\branding'
New-Item -ItemType Directory -Force $branding | Out-Null
$svg = [IO.File]::ReadAllText([IO.Path]::GetFullPath($Source))
foreach ($variant in @(@('white', '#FFFFFF'), @('black', '#000000'))) {
    $colored = [regex]::Replace($svg, '(fill|stroke|stop-color)="(?!none)[^"]*"', ('$1="' + $variant[1] + '"'))
    [IO.File]::WriteAllText((Join-Path $branding "icon-$($variant[0]).svg"), $colored)
}
$white = [IO.Path]::GetFullPath((Join-Path $branding 'icon-white.svg'))
$black = [IO.Path]::GetFullPath((Join-Path $branding 'icon-black.svg'))
winapp manifest update-assets $white --light-image $black --manifest (Join-Path $project 'Package.appxmanifest')
if ($LASTEXITCODE) { throw 'Asset generation failed.' }
$temporary = Join-Path $branding 'light-icon-layout'
New-Item -ItemType Directory -Force (Join-Path $temporary 'Assets') | Out-Null
try {
    Copy-Item (Join-Path $project 'Package.appxmanifest') $temporary
    Copy-Item (Join-Path $project 'Assets\AppIcon.ico') (Join-Path $temporary 'Assets')
    winapp manifest update-assets $black --manifest (Join-Path $temporary 'Package.appxmanifest')
    if ($LASTEXITCODE) { throw 'Light tray icon generation failed.' }
    Copy-Item (Join-Path $temporary 'Assets\AppIcon.ico') (Join-Path $project 'Assets\AppIcon-dark.ico')
} finally {
    Remove-Item -LiteralPath $temporary -Recurse -Force
}
