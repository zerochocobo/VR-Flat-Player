#Requires -Version 5.1
<#
.SYNOPSIS
    Puts VR Flat Player in the "Open with" list for video files.

.DESCRIPTION
    Writes only under HKCU, so no administrator rights are needed and nothing
    changes for other users on the machine.

    Three keys are needed, and missing any one of them produces a different
    kind of half-registration:

      Software\Classes\Applications\VRFlatPlayer.exe
          What the "Open with > Choose another app" browser reads. Without it
          the player is invisible there no matter what else is registered.

      Software\Classes\VRFlatPlayer.Video           (the ProgId)
          The launch command and the icon. This is what actually opens files.

      Software\Classes\.mp4\OpenWithProgids\...     (one per extension)
          Puts the ProgId on the short "Open with" flyout for that extension,
          without touching whichever player is currently the default.

    This deliberately does NOT make itself the default player. Windows 10 and
    11 block programmatic default-setting on purpose -- the user has to pick it
    once in the Open-with dialog and tick "Always use this app".

    The exe path is written into the registry verbatim, so moving or renaming
    the player's folder breaks the association. Re-run this script afterwards.

.PARAMETER Unregister
    Removes everything this script added.

.PARAMETER ExePath
    Which VRFlatPlayer.exe to point at. Auto-detected if omitted.

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File tools\register-file-association.ps1

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File tools\register-file-association.ps1 -Unregister
#>
[CmdletBinding()]
param(
    [switch]$Unregister,
    [string]$ExePath
)

$ErrorActionPreference = 'Stop'

$ProgId     = 'VRFlatPlayer.Video'
$AppName    = 'VR Flat Player'
$AppKey     = 'VRFlatPlayer.exe'
$Extensions = @('.mp4', '.mkv', '.webm', '.mov', '.m4v', '.avi', '.wmv', '.ts', '.m2ts', '.mpg', '.mpeg', '.flv')

$classes = 'HKCU:\Software\Classes'
$appRoot = "$classes\Applications\$AppKey"

# Explorer caches associations; without this the change does not show up until
# the shell is restarted or the user signs out.
function Invoke-ShellRefresh {
    if (-not ('VrfpShell' -as [type])) {
        Add-Type -Namespace '' -Name 'VrfpShell' -MemberDefinition @'
[System.Runtime.InteropServices.DllImport("shell32.dll")]
public static extern void SHChangeNotify(int eventId, uint flags, System.IntPtr item1, System.IntPtr item2);
'@
    }
    # SHCNE_ASSOCCHANGED, SHCNF_IDLIST
    [VrfpShell]::SHChangeNotify(0x08000000, 0x0000, [System.IntPtr]::Zero, [System.IntPtr]::Zero)
}

# ------------------------------------------------------------------ remove ---

if ($Unregister) {
    foreach ($key in @("$classes\$ProgId", $appRoot)) {
        if (Test-Path $key) { Remove-Item $key -Recurse -Force; Write-Host "removed $key" }
    }
    foreach ($ext in $Extensions) {
        $key = "$classes\$ext\OpenWithProgids"
        if (-not (Test-Path $key)) { continue }
        try { Remove-ItemProperty -Path $key -Name $ProgId -ErrorAction Stop; Write-Host "removed from $ext" }
        catch { }
    }
    Invoke-ShellRefresh
    Write-Host ''
    Write-Host 'Unregistered.'
    return
}

# ------------------------------------------------------- find the launcher ---

if (-not $ExePath) {
    $repoRoot = Split-Path -Parent $PSScriptRoot

    # Newest published folder first: dist\VR Flat Player 0.1\, and so on. That
    # is the copy a user actually runs, and it is the one the old version of
    # this script could not see.
    $published = @()
    $dist = Join-Path $repoRoot 'dist'
    if (Test-Path $dist) {
        $published = Get-ChildItem $dist -Directory -ErrorAction SilentlyContinue |
            ForEach-Object { Join-Path $_.FullName 'VRFlatPlayer.exe' } |
            Where-Object { Test-Path $_ } |
            Sort-Object { (Get-Item $_).LastWriteTime } -Descending
    }

    $candidates = @(
        # Shipped layout: this script sits beside the exe, not under tools\.
        (Join-Path $PSScriptRoot 'VRFlatPlayer.exe')
    ) + $published + @(
        (Join-Path $repoRoot 'VRFlatPlayer.exe'),
        (Join-Path $repoRoot 'src\HeadTrackBridge\bin\Release\net8.0-windows\VRFlatPlayer.exe'),
        (Join-Path $repoRoot 'src\HeadTrackBridge\bin\Debug\net8.0-windows\VRFlatPlayer.exe')
    )

    $ExePath = $candidates | Where-Object { Test-Path $_ } | Select-Object -First 1
}

if (-not $ExePath -or -not (Test-Path $ExePath)) {
    throw "Could not find VRFlatPlayer.exe. Run publish.bat first, or pass -ExePath."
}
$ExePath = (Resolve-Path $ExePath).Path
$command = "`"$ExePath`" `"%1`""
$icon    = "`"$ExePath`",0"

Write-Host "launcher: $ExePath"

# ------------------------------------------------------------- the ProgId ---

New-Item -Path "$classes\$ProgId\shell\open\command" -Force | Out-Null
Set-ItemProperty -Path "$classes\$ProgId"                    -Name '(default)' -Value "$AppName Video"
Set-ItemProperty -Path "$classes\$ProgId"                    -Name 'FriendlyTypeName' -Value "$AppName Video"
Set-ItemProperty -Path "$classes\$ProgId\shell\open"         -Name '(default)' -Value "Open with $AppName"
Set-ItemProperty -Path "$classes\$ProgId\shell\open\command" -Name '(default)' -Value $command

New-Item -Path "$classes\$ProgId\DefaultIcon" -Force | Out-Null
Set-ItemProperty -Path "$classes\$ProgId\DefaultIcon" -Name '(default)' -Value $icon

# -------------------------------------------- the application registration ---
# This is the half that was missing, and the reason the player never appeared
# under "Choose another app".

New-Item -Path "$appRoot\shell\open\command" -Force | Out-Null
Set-ItemProperty -Path $appRoot                    -Name 'FriendlyAppName' -Value $AppName
Set-ItemProperty -Path "$appRoot\shell\open\command" -Name '(default)' -Value $command

New-Item -Path "$appRoot\DefaultIcon" -Force | Out-Null
Set-ItemProperty -Path "$appRoot\DefaultIcon" -Name '(default)' -Value $icon

# SupportedTypes is what stops Windows offering the player for .txt and .exe.
New-Item -Path "$appRoot\SupportedTypes" -Force | Out-Null
foreach ($ext in $Extensions) {
    Set-ItemProperty -Path "$appRoot\SupportedTypes" -Name $ext -Value ''
}

# ------------------------------------------------- per-extension Open with ---

foreach ($ext in $Extensions) {
    $key = "$classes\$ext\OpenWithProgids"
    New-Item -Path $key -Force | Out-Null
    # An empty REG_NONE value is the documented way to list a ProgId here.
    New-ItemProperty -Path $key -Name $ProgId -PropertyType None -Value ([byte[]]@()) -Force | Out-Null
}

Invoke-ShellRefresh

Write-Host ''
Write-Host "Registered for: $($Extensions -join ' ')"
Write-Host ''
Write-Host 'Right-click a video > Open with, and "VR Flat Player" is now in the list.'
Write-Host 'To make it the default, pick it there and tick "Always use this app".'
Write-Host 'Windows does not let an application set that for itself.'
Write-Host ''
Write-Host 'The exe path above is baked into the registry. If you move the player'
Write-Host 'folder, run this script again.'
