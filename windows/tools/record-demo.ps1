<#
.SYNOPSIS
  Records the scripted demo (appear, song change, line changes, interlude, no lyrics, pause, disappear)
  off screen and turns it into a GIF. Needs ffmpeg (winget install Gyan.FFmpeg).

.EXAMPLE
  ./windows/tools/record-demo.ps1 -Out docs/screenshots/windows/transitions.gif
#>
param(
    [string]$Out = "transitions.gif",
    [string]$Configuration = "Release",
    [string]$Ffmpeg = "ffmpeg"
)
$ErrorActionPreference = "Stop"
$project = Join-Path $PSScriptRoot "..\LyricsIsland.Windows\LyricsIsland.Windows.csproj"
$frames = Join-Path ([IO.Path]::GetTempPath()) "LyricsIslandFrames"

dotnet build $project -c $Configuration --nologo -v quiet | Out-Null
$exe = Join-Path $PSScriptRoot "..\LyricsIsland.Windows\bin\$Configuration\net8.0-windows10.0.19041.0\LyricsIsland.exe"
Start-Process $exe -ArgumentList "--record `"$frames`"" -Wait

# One palette for the whole clip; the background is flat, so 128 colors are plenty.
& $Ffmpeg -hide_banner -loglevel error -y -framerate 25 -i (Join-Path $frames "frame-%04d.png") `
    -vf "split[a][b];[a]palettegen=max_colors=128:stats_mode=full[p];[b][p]paletteuse=dither=none" `
    -loop 0 $Out
Remove-Item $frames -Recurse
Get-Item $Out | Select-Object Name, Length
