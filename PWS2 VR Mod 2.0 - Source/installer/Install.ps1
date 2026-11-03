# PowerWash VR - installer
#
# Puts three things into the PowerWash Simulator 2 folder:
#   1. MelonLoader v0.7.3 (the mod loader)            - from its GitHub release
#   2. Unity's OpenXR plugin 1.18.0 (three files)     - from Unity's package registry
#   3. Mods\PowerWashVR.dll (this mod)                - from this folder
#
# Every download is checked against a known SHA-256 before anything is copied.
# Nothing is installed outside the game folder. Run Uninstall.ps1 to undo.
#
#   powershell -ExecutionPolicy Bypass -File Install.ps1 [-GamePath "D:\...\PowerWash Simulator 2"]

param(
    [string] $GamePath = ""
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12

$Here = Split-Path -Parent $MyInvocation.MyCommand.Path
$ExeName = 'PowerWash Simulator 2.exe'

$MelonLoader = @{
    Url    = 'https://github.com/LavaGang/MelonLoader/releases/download/v0.7.3/MelonLoader.x64.zip'
    Sha256 = '5B2B2F3D1CD42B59EC886C5BDC2663EDAE87A0097A4F4A8F58C0965A99DDA416'
}

$OpenXrPackage = 'https://download.packages.unity.com/com.unity.xr.openxr/-/com.unity.xr.openxr-1.18.0.tgz'
$OpenXrMirror  = 'https://github.com/needle-mirror/com.unity.xr.openxr/raw/1.18.0/'
$OpenXrFiles = @(
    @{ Source = 'RuntimeLoaders/windows/x64/openxr_loader.dll'
       Target = 'PowerWash Simulator 2_Data\Plugins\x86_64\openxr_loader.dll'
       Sha256 = 'F7384429C16288D9C2C123ACFCA1E040C9CEC6C5484A3F5A5107D6AA32BA57E8' },
    @{ Source = 'Runtime/windows/x64/UnityOpenXR.dll'
       Target = 'PowerWash Simulator 2_Data\Plugins\x86_64\UnityOpenXR.dll'
       Sha256 = 'A344BF5428D70D41E91BD36AA85677206A66224949F9C830D8DED4F1D612BE66' },
    @{ Source = 'Runtime/UnitySubsystemsManifest.json'
       Target = 'PowerWash Simulator 2_Data\UnitySubsystems\UnityOpenXR\UnitySubsystemsManifest.json'
       Sha256 = '2528D0CAA5FB4C4EA91ED74FB88315FCA934158314BCEC29D12FBEAAC8E7CFD6' }
)

function Say([string] $Text, [string] $Colour = 'Gray') { Write-Host $Text -ForegroundColor $Colour }
function Fail([string] $Text) { Write-Host ''; Write-Host $Text -ForegroundColor Red; Write-Host ''; exit 1 }
function Hash([string] $Path) { (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToUpperInvariant() }

function Download([string] $Url, [string] $To) {
    Say "  downloading $Url"
    $client = New-Object Net.WebClient
    $client.Headers.Add('User-Agent', 'PowerWashVR-Installer')
    $client.DownloadFile($Url, $To)
}

function Find-Game {
    if ($GamePath) { return $GamePath }
    if (Test-Path -LiteralPath (Join-Path $Here $ExeName)) { return $Here }
    $parent = Split-Path -Parent $Here
    if ($parent -and (Test-Path -LiteralPath (Join-Path $parent $ExeName))) { return $parent }

    $steam = $null
    foreach ($key in 'HKCU:\Software\Valve\Steam', 'HKLM:\SOFTWARE\WOW6432Node\Valve\Steam') {
        try {
            $item = Get-ItemProperty -Path $key -ErrorAction Stop
            if ($item.PSObject.Properties['SteamPath']) { $steam = $item.SteamPath }
            elseif ($item.PSObject.Properties['InstallPath']) { $steam = $item.InstallPath }
            if ($steam) { break }
        } catch { }
    }
    if (-not $steam) { return $null }

    $libraries = @($steam)
    $vdf = Join-Path $steam 'steamapps\libraryfolders.vdf'
    if (Test-Path -LiteralPath $vdf) {
        foreach ($line in Get-Content -LiteralPath $vdf) {
            if ($line -match '"path"\s+"([^"]+)"') { $libraries += ($Matches[1] -replace '\\\\', '\') }
        }
    }
    foreach ($library in $libraries) {
        $candidate = Join-Path $library 'steamapps\common\PowerWash Simulator 2'
        if (Test-Path -LiteralPath (Join-Path $candidate $ExeName)) { return $candidate }
    }
    return $null
}

Say ''
Say 'PowerWash VR installer' 'Cyan'
Say ''

$game = Find-Game
if (-not $game -or -not (Test-Path -LiteralPath (Join-Path $game $ExeName))) {
    Fail "PowerWash Simulator 2 was not found. Run again with -GamePath ""<folder that contains $ExeName>""."
}
$game = (Resolve-Path -LiteralPath $game).Path
Say "Game folder: $game"

if (Get-Process -Name 'PowerWash Simulator 2' -ErrorAction SilentlyContinue) {
    Fail 'Close PowerWash Simulator 2 first.'
}

$mod = Join-Path $Here 'Mods\PowerWashVR.dll'
if (-not (Test-Path -LiteralPath $mod)) { Fail "Mods\PowerWashVR.dll is missing next to this script." }

$temp = Join-Path ([IO.Path]::GetTempPath()) ('PowerWashVR-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $temp | Out-Null

try {
    # 1. MelonLoader
    $loader = Join-Path $game 'MelonLoader\net6\MelonLoader.dll'
    $haveLoader = (Test-Path -LiteralPath $loader) -and
        ((Get-Item -LiteralPath $loader).VersionInfo.FileVersion -like '0.7.3*') -and
        (Test-Path -LiteralPath (Join-Path $game 'version.dll'))
    if ($haveLoader) {
        Say 'MelonLoader 0.7.3: already installed'
    } else {
        Say 'MelonLoader 0.7.3:'
        $zip = Join-Path $temp 'MelonLoader.x64.zip'
        Download $MelonLoader.Url $zip
        if ((Hash $zip) -ne $MelonLoader.Sha256) { Fail 'The MelonLoader download does not match its known checksum. Nothing was changed.' }
        Expand-Archive -LiteralPath $zip -DestinationPath $game -Force
        Say '  installed' 'Green'
    }

    # 2. OpenXR
    $needed = @($OpenXrFiles | Where-Object {
        $target = Join-Path $game $_.Target
        -not (Test-Path -LiteralPath $target) -or (Hash $target) -ne $_.Sha256
    })
    if ($needed.Count -eq 0) {
        Say 'Unity OpenXR 1.18.0: already installed'
    } else {
        Say 'Unity OpenXR 1.18.0:'
        $root = Join-Path $temp 'openxr'
        New-Item -ItemType Directory -Path $root | Out-Null
        $fromPackage = $false
        try {
            $tgz = Join-Path $temp 'openxr.tgz'
            Download $OpenXrPackage $tgz
            & tar.exe -xzf $tgz -C $root
            if ($LASTEXITCODE -eq 0) { $fromPackage = $true }
        } catch {
            Say "  Unity's registry was not reachable, trying the GitHub mirror" 'Yellow'
        }

        foreach ($file in $needed) {
            $source = if ($fromPackage) { Join-Path $root ('package/' + $file.Source) } else { $null }
            if (-not $source -or -not (Test-Path -LiteralPath $source)) {
                $source = Join-Path $root ([IO.Path]::GetFileName($file.Source))
                Download ($OpenXrMirror + $file.Source) $source
            }
            if ((Hash $source) -ne $file.Sha256) { Fail "$($file.Source) does not match its known checksum. Nothing more was changed." }
            $target = Join-Path $game $file.Target
            New-Item -ItemType Directory -Path (Split-Path -Parent $target) -Force | Out-Null
            Copy-Item -LiteralPath $source -Destination $target -Force
        }
        Say '  installed' 'Green'
    }

    # 3. The mod
    $mods = Join-Path $game 'Mods'
    New-Item -ItemType Directory -Path $mods -Force | Out-Null
    Copy-Item -LiteralPath $mod -Destination (Join-Path $mods 'PowerWashVR.dll') -Force
    Say 'PowerWash VR: installed' 'Green'
}
finally {
    Remove-Item -LiteralPath $temp -Recurse -Force -ErrorAction SilentlyContinue
}

# MelonLoader runs on the .NET 6 runtime.
$dotnet = Join-Path $env:ProgramFiles 'dotnet\shared\Microsoft.NETCore.App'
$hasNet6 = (Test-Path -LiteralPath $dotnet) -and @(Get-ChildItem -LiteralPath $dotnet -Directory | Where-Object { $_.Name -like '6.*' }).Count -gt 0
if (-not $hasNet6) {
    Say ''
    Say 'MelonLoader needs the .NET 6 runtime (x64), which is not installed:' 'Yellow'
    Say '  https://dotnet.microsoft.com/download/dotnet/6.0' 'Yellow'
}

Say ''
Say 'Done. Start SteamVR (or your headset software), then start the game from Steam.' 'Cyan'
Say 'The first launch takes a minute while MelonLoader prepares itself.'
Say ''
