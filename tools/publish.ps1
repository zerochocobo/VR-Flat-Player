<#
.SYNOPSIS
  Builds a distributable VR Flat Player folder (and zip).

.DESCRIPTION
  Produces the layout the app expects at runtime:

      VR Flat Player/             (no version: see the comment on $stage)
        VRFlatPlayer.exe          our player
        bridge.config.json        default settings
        register-file-types.bat   adds the player to Explorer's "Open with"
        unregister-file-types.bat
        register-file-association.ps1
        README.md
        THIRD-PARTY-NOTICES.txt
        mpv/
          mpv.exe                 the bundled player engine
          d3dcompiler_43.dll
          mpv.conf, input.conf, scripts/, shaders/, fonts/, script-opts/

  mpv.exe lives inside mpv/ deliberately: that directory is both the engine and
  its --config-dir, so "the mpv we ship" is one folder the user can delete or
  replace as a unit, and MpvLauncher.ResolveMpvPath already looks there first.

.EXAMPLE
  powershell -ExecutionPolicy Bypass -File tools\publish.ps1

.EXAMPLE
  # Tiny download, but every user needs the .NET 8 Desktop Runtime installed.
  powershell -ExecutionPolicy Bypass -File tools\publish.ps1 -FrameworkDependent
#>
[CmdletBinding()]
param(
    [string] $Runtime = 'win-x64',

    # Bundles the .NET runtime so the player runs on a machine with no .NET at
    # all. Costs ~40 MB compressed and is the right default for a release.
    [switch] $FrameworkDependent,

    # One exe instead of a folder of ~200 DLLs. Off only when you need to poke
    # at the individual assemblies.
    [switch] $NoSingleFile,

    # Where to take mpv.exe from. Defaults to whatever mpv is on this machine.
    [string] $MpvExe,

    [string] $Output = 'dist',

    [switch] $NoZip
)

$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$proj = Join-Path $repo 'src\HeadTrackBridge\HeadTrackBridge.csproj'

# ---------------------------------------------------------------- version ---
# Read from the csproj so the folder, the zip and the About box can never
# disagree. There is one place to edit the version and this is not it.
[xml] $csproj = Get-Content $proj
$version = ($csproj.Project.PropertyGroup.InformationalVersion | Where-Object { $_ }) -as [string]
if (-not $version) { throw "No <InformationalVersion> in $proj." }
Write-Host "version: $version"

# The plain name is the normal x64 self-contained download. Any other build is
# a different artifact for a different machine, so it gets its own folder and
# zip — otherwise publishing an ARM64 or framework-dependent copy silently
# overwrites the one you just made and you ship the wrong file.
$suffix = ''
if ($Runtime -ne 'win-x64') { $suffix += " $Runtime" }
if ($FrameworkDependent)    { $suffix += ' net8-required' }

# No version in the folder name, on purpose. Explorer's "Open with" stores an
# absolute path to the exe, so a folder that changes name every release breaks
# the association on every release and leaves the old build lying around to be
# launched by accident. One stable folder, overwritten each time — it is cleared
# below before anything is copied in, so nothing stale survives.
#
# The zip keeps the version: that one is an artifact you hand to someone, and it
# has to say which build it is.
$stage = Join-Path $repo "$Output\VR Flat Player$suffix"

# ------------------------------------------------------------- find mpv ---
if (-not $MpvExe) {
    $candidates = @(
        (Join-Path $repo 'third_party\mpv\mpv.exe'),
        (Join-Path ${env:ProgramFiles} 'MPV Player\mpv.exe'),
        (Join-Path ${env:ProgramFiles} 'mpv\mpv.exe'),
        (Join-Path ${env:LOCALAPPDATA} 'Programs\mpv\mpv.exe')
    )
    $cmd = Get-Command mpv.exe -ErrorAction SilentlyContinue
    if ($cmd) { $candidates = @($cmd.Source) + $candidates }
    $MpvExe = $candidates | Where-Object { Test-Path $_ } | Select-Object -First 1
}
if (-not $MpvExe -or -not (Test-Path $MpvExe)) {
    throw "Could not find mpv.exe. Install mpv, or pass -MpvExe <path>."
}
Write-Host "mpv:     $MpvExe"

# uosc and the shader are downloaded by install-mpv360.ps1 and are not in git.
# Shipping without them produces a player with no control bar and no 360, which
# is not obvious until someone opens a file, so fail here instead.
foreach ($required in @('scripts\uosc\main.lua', 'scripts\mpv360.lua', 'shaders\mpv360.glsl', 'fonts')) {
    if (-not (Test-Path (Join-Path $repo "mpv\$required"))) {
        throw "Missing mpv\$required. Run tools\install-mpv360.ps1 first."
    }
}

# ----------------------------------------------------------------- build ---
# Empty the staging directory rather than deleting it. Anything holding the
# directory itself — an Explorer window, a shell sitting in it, a virus scanner
# — makes the delete fail, and re-running a publish should not require hunting
# that down.
if (Test-Path $stage) {
    Get-ChildItem $stage -Force | Remove-Item -Recurse -Force
} else {
    New-Item -ItemType Directory -Path $stage -Force | Out-Null
}

$publishArgs = @(
    'publish', $proj,
    '-c', 'Release',
    '-r', $Runtime,
    '-o', $stage,
    '--nologo'
)
if ($FrameworkDependent) {
    $publishArgs += '--self-contained:false'
} else {
    $publishArgs += '--self-contained:true'
    if (-not $NoSingleFile) {
        $publishArgs += @(
            '-p:PublishSingleFile=true',
            '-p:IncludeNativeLibrariesForSelfExtract=true',
            # Roughly halves the download. Costs a fraction of a second on the
            # first launch, which is nothing next to mpv starting up.
            '-p:EnableCompressionInSingleFile=true'
        )
    }
}

Write-Host ''
Write-Host "dotnet $($publishArgs -join ' ')"
& dotnet @publishArgs
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed ($LASTEXITCODE)." }

# Debug symbols are not part of a release download.
Get-ChildItem $stage -Filter *.pdb -Recurse | Remove-Item -Force

# ------------------------------------------------------------- bundle mpv ---
$mpvOut = Join-Path $stage 'mpv'
New-Item -ItemType Directory -Path $mpvOut -Force | Out-Null

# watch_later is per-user playback state; shaders-src is our shader fork's
# source, kept in the release because the MIT licence expects it to be findable.
$exclude = @('watch_later')
Get-ChildItem (Join-Path $repo 'mpv') |
    Where-Object { $exclude -notcontains $_.Name } |
    ForEach-Object { Copy-Item $_.FullName -Destination $mpvOut -Recurse -Force }

Copy-Item $MpvExe -Destination $mpvOut -Force
$d3d = Join-Path (Split-Path -Parent $MpvExe) 'd3dcompiler_43.dll'
if (Test-Path $d3d) { Copy-Item $d3d -Destination $mpvOut -Force }

# Generated from the compiled defaults, never copied from the repo.
#
# The hand-maintained copy had gone stale, and stale is worse than missing here:
# a shipped config is read in full, so every key it still carries silently
# overrides the default that replaced it. It was shipping yaw deadzone 0.6 and
# no stickyDegrees long after those defaults changed, which switched the
# anti-shake work off for everyone running the release while the source said it
# was on. Nothing to keep in step if nobody maintains it.
#
# --config points at a path that does not exist, so BridgeConfig.Load returns a
# fresh object rather than round-tripping whatever is already installed.
$fresh = Join-Path ([System.IO.Path]::GetTempPath()) "vrfp-default-$PID.json"
# Start-Process -Wait, not the call operator: VRFlatPlayer is a WinExe, and
# PowerShell does not wait for a GUI-subsystem process invoked with &, so the
# check below would run before the file existed.
Start-Process -Wait -NoNewWindow -FilePath (Join-Path $stage 'VRFlatPlayer.exe') `
              -ArgumentList @("`"--config=$fresh`"", '--write-config') | Out-Null
if (-not (Test-Path $fresh)) { throw "Could not generate bridge.config.json from the defaults." }
Move-Item $fresh (Join-Path $stage 'bridge.config.json') -Force

# Files the player writes as it runs. None of them belong in a release: they
# describe the machine that happened to run it last, which during development is
# always the developer's.
#
# This is not hypothetical. A build went out carrying a 1391x1530 window saved
# from a 288 dpi development display; on the user's 1080p screen it opened
# clamped to full height, and the log said "remembered" for a placement they had
# never chosen. Window state has since moved out of bridge.config.json into its
# own file precisely so it can be caught here — a settings file that has to ship
# cannot simply be deleted.
$script:RuntimeState = @('window-state.json', 'mode-memory.json', 'mpv-last-run.log')

function Remove-RuntimeState($dir) {
    foreach ($name in $script:RuntimeState) {
        $p = Join-Path $dir $name
        if (Test-Path $p) { Remove-Item $p -Force; Write-Host "scrubbed: $name" }
    }
}
Remove-RuntimeState $stage

Copy-Item (Join-Path $repo 'README.md') -Destination $stage -Force

# "Open with" registration, for users who never see this repository. The .ps1
# goes to the stage root rather than a tools\ subfolder because it locates the
# player by looking beside itself.
# The face detector, if it has been fetched. Not fatal when missing: head
# tracking is optional, and the player says what to run if you turn it on
# without the model.
$models = Join-Path $repo 'models'
if (Test-Path $models) {
    Copy-Item $models -Destination $stage -Recurse -Force
    Write-Host "models: bundled ($(@(Get-ChildItem $models -File).Count) file(s))"
} else {
    Write-Host 'models: none found (run tools\install-models.bat for webcam head tracking)'
}

Copy-Item (Join-Path $repo 'tools\register-file-association.ps1') -Destination $stage -Force
Copy-Item (Join-Path $repo 'register-file-types.bat') -Destination $stage -Force
Copy-Item (Join-Path $repo 'unregister-file-types.bat') -Destination $stage -Force

# ---------------------------------------------------------------- notices ---
# mpv is GPL. Redistributing its binary means the release has to say which
# build it is and where its source lives; naming the exact version string is
# what makes that offer meaningful.
# Just the build id. The rest of mpv's banner is a copyright line containing a
# (c) sign, and PowerShell 5.1 reads a native command's output as ANSI, so
# carrying it through would put mojibake in the release notes.
$mpvVersion = 'unknown build'
$banner = (& $MpvExe --version 2>$null | Select-Object -First 1)
if ($banner -match '^mpv\s+(\S+)') { $mpvVersion = $Matches[1] }

@"
$($csproj.Project.PropertyGroup.Product | Where-Object { $_ }) $version
Open source, non-commercial.

This release bundles third-party software:

mpv  ($mpvVersion)
  GPLv2-or-later / LGPLv2.1-or-later.  Bundled unmodified as mpv\mpv.exe.
  Source: https://github.com/mpv-player/mpv
  Windows build: https://github.com/shinchiro/mpv-winbuild-cmake
  The player runs mpv as a separate process and talks to it over its JSON IPC
  interface; no mpv code is linked into VRFlatPlayer.exe.

mpv360  (MIT)  -  360 projection shader
  Source: https://github.com/kasper93/mpv360
  mpv\mpv360.LICENSE holds the licence text. Our fork of the shader, which adds
  two projections, is in mpv\shaders-src\mpv360-vr.glsl.

uosc  (LGPL-2.1)  -  control bar
  Source: https://github.com/tomasklaen/uosc
  Bundled unmodified in mpv\scripts\uosc.

One Euro Filter  -  Casiez, Roussel, Vogel (CHI 2012), reimplemented here.
"@ | ForEach-Object {
    # Not Set-Content -Encoding UTF8: that writes a BOM in PowerShell 5.1, which
    # shows up as stray characters in editors that assume plain ASCII.
    [System.IO.File]::WriteAllText(
        (Join-Path $stage 'THIRD-PARTY-NOTICES.txt'), $_,
        (New-Object System.Text.UTF8Encoding $false))
}

# -------------------------------------------------------------------- zip ---
$size = '{0:N0} MB' -f ((Get-ChildItem $stage -Recurse -File | Measure-Object Length -Sum).Sum / 1MB)
Write-Host ''
Write-Host "staged: $stage  ($size)"

# Again, immediately before packing. Anything between the build and here that
# starts the player -- a smoke test, an accidental double-click -- recreates
# these, and the zip is the artifact that reaches other people.
Remove-RuntimeState $stage

if (-not $NoZip) {
    # Versioned, unlike the folder: this is the file that gets handed to someone
    # else, and it has to say which build it is.
    $zip = "$stage $version.zip"
    if (Test-Path $zip) { Remove-Item $zip -Force }
    # ZipFile rather than Compress-Archive. Compress-Archive started failing
    # here with UnauthorizedAccessError on a folder nothing had open -- it is
    # known to be fragile on large trees, and it is also several times slower.
    # The .NET API packs the same folder without complaint.
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    [System.IO.Compression.ZipFile]::CreateFromDirectory(
        $stage, $zip, [System.IO.Compression.CompressionLevel]::Optimal, $true)
    $zipSize = '{0:N0} MB' -f ((Get-Item $zip).Length / 1MB)
    Write-Host "zip:    $zip  ($zipSize)"
}

Write-Host ''
Write-Host 'Smoke-test the staged copy before shipping it:'
Write-Host "  `"$stage\VRFlatPlayer.exe`" --version"
Write-Host "  `"$stage\VRFlatPlayer.exe`" <some-video>"
