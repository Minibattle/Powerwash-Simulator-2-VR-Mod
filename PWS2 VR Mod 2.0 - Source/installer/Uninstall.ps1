# PowerWash VR - uninstaller
#
# Removes the mod and the three OpenXR files. MelonLoader stays unless you pass
# -RemoveMelonLoader, because other mods may use it. Your settings in
# UserData\MelonPreferences.cfg are left alone.
#
#   powershell -ExecutionPolicy Bypass -File Uninstall.ps1 [-GamePath "..."] [-RemoveMelonLoader]

param(
    [string] $GamePath = "",
    [switch] $RemoveMelonLoader
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$Here = Split-Path -Parent $MyInvocation.MyCommand.Path
$ExeName = 'PowerWash Simulator 2.exe'

function Say([string] $Text, [string] $Colour = 'Gray') { Write-Host $Text -ForegroundColor $Colour }

if (-not $GamePath) {
    foreach ($candidate in @($Here, (Split-Path -Parent $Here))) {
        if ($candidate -and (Test-Path -LiteralPath (Join-Path $candidate $ExeName))) { $GamePath = $candidate; break }
    }
}
if (-not $GamePath -or -not (Test-Path -LiteralPath (Join-Path $GamePath $ExeName))) {
    Write-Host "Run with -GamePath ""<folder that contains $ExeName>""." -ForegroundColor Red
    exit 1
}

if (Get-Process -Name 'PowerWash Simulator 2' -ErrorAction SilentlyContinue) {
    Write-Host 'Close PowerWash Simulator 2 first.' -ForegroundColor Red
    exit 1
}

$remove = @(
    'Mods\PowerWashVR.dll',
    'PowerWash Simulator 2_Data\Plugins\x86_64\UnityOpenXR.dll',
    'PowerWash Simulator 2_Data\Plugins\x86_64\openxr_loader.dll',
    'PowerWash Simulator 2_Data\UnitySubsystems\UnityOpenXR'
)
if ($RemoveMelonLoader) { $remove += @('version.dll', 'MelonLoader') }

foreach ($relative in $remove) {
    $path = Join-Path $GamePath $relative
    if (Test-Path -LiteralPath $path) {
        Remove-Item -LiteralPath $path -Recurse -Force
        Say "removed $relative"
    }
}

Say 'PowerWash VR removed. The game runs flat again.' 'Cyan'
