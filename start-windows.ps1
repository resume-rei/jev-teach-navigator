param([switch]$Rebuild, [switch]$TeachingLab)
$ErrorActionPreference = 'Stop'
$executable = Join-Path $PSScriptRoot 'windows\bin\JevTeachWindows.exe'
$sources = Get-ChildItem -LiteralPath (Join-Path $PSScriptRoot 'windows') -File | Where-Object { $_.Extension -in '.cs', '.ps1' }
$needsBuild = $Rebuild -or !(Test-Path -LiteralPath $executable)
if (!$needsBuild) {
    $builtAt = (Get-Item -LiteralPath $executable).LastWriteTimeUtc
    $needsBuild = @($sources | Where-Object { $_.LastWriteTimeUtc -gt $builtAt }).Count -gt 0
}
if ($needsBuild) { & (Join-Path $PSScriptRoot 'windows\build.ps1') }
# This is the interactive application the user explicitly chose to open.
if ($TeachingLab) {
    Start-Process -FilePath $executable -ArgumentList '--teaching-lab' -WorkingDirectory $PSScriptRoot -WindowStyle Normal
} else {
    Start-Process -FilePath $executable -WorkingDirectory $PSScriptRoot -WindowStyle Normal
}
