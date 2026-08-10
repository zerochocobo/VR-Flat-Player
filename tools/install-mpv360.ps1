#Requires -Version 5.1
<#
.SYNOPSIS
    Installs the mpv360 Lua script (kasper93/mpv360, MIT) and this project's
    forked shader into the repo's mpv config directory.

.DESCRIPTION
    The Lua script is fetched from upstream and lightly patched to know about
    the two projections our shader fork adds.

    The SHADER is not fetched — mpv/shaders-src/mpv360-vr.glsl is our own fork
    (see its header for what changed and why) and is copied into place. It is
    vendored rather than patched because the changes are interdependent and a
    half-applied patch would render the wrong thing without failing.

    Generated files land in mpv\scripts and mpv\shaders, which .gitignore
    excludes. The fork source in mpv\shaders-src IS tracked.
#>
[CmdletBinding()]
param(
    [string]$Ref = 'master',
    [switch]$Force
)

$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent $PSScriptRoot
$mpvDir   = Join-Path $repoRoot 'mpv'
$luaPath  = Join-Path $mpvDir 'scripts\mpv360.lua'

# Every patch below goes through these two, and nothing here may use
# Get-Content/Set-Content on a source file. Windows PowerShell 5.1 reads with
# -Raw as ANSI unless told otherwise, and writes -Encoding UTF8 *with a BOM*.
# Round-tripping a UTF-8 Lua file through that pair corrupts every non-ASCII
# character in it and prepends a BOM — which has already broken this project
# twice: uosc stopped loading entirely ("')' expected" on a mangled apostrophe),
# and mpv360's degree sign turned into a replacement character. These read and
# write the exact bytes.
function Read-Utf8([string] $path) {
    return [System.Text.Encoding]::UTF8.GetString([System.IO.File]::ReadAllBytes($path))
}

function Write-Utf8([string] $path, [string] $text) {
    [System.IO.File]::WriteAllText($path, $text, (New-Object System.Text.UTF8Encoding $false))
}

foreach ($d in @('scripts','shaders')) {
    $p = Join-Path $mpvDir $d
    if (-not (Test-Path $p)) { New-Item -ItemType Directory -Path $p -Force | Out-Null }
}

# ---------------------------------------------------------------- upstream ---

$downloads = @(
    @{ Url = "https://raw.githubusercontent.com/kasper93/mpv360/$Ref/scripts/mpv360.lua"; Dest = $luaPath }
    # NOT in scripts\ — mpv tries to load every file in that directory as a script.
    @{ Url = "https://raw.githubusercontent.com/kasper93/mpv360/$Ref/LICENSE";            Dest = Join-Path $mpvDir 'mpv360.LICENSE' }
)

foreach ($f in $downloads) {
    if ((Test-Path $f.Dest) -and -not $Force) {
        Write-Host "skip (exists): $($f.Dest)"
        continue
    }
    Write-Host "get  $($f.Url)"
    Invoke-WebRequest -Uri $f.Url -OutFile $f.Dest -UseBasicParsing
}

# ------------------------------------------------------------------ shader ---

$forkSrc = Join-Path $mpvDir 'shaders-src\mpv360-vr.glsl'
$forkDst = Join-Path $mpvDir 'shaders\mpv360.glsl'   # the .lua hardcodes this path
if (-not (Test-Path $forkSrc)) { throw "Missing shader fork: $forkSrc" }
Copy-Item $forkSrc $forkDst -Force
Write-Host "shader: installed fork from shaders-src\mpv360-vr.glsl"

# --------------------------------------------------------------- lua patch ---
# Teach the script the two appended projections so its own Ctrl+Shift+p cycling
# and its OSD labels stay in sync with the shader.

$lua = Read-Utf8 $luaPath
$patched = $false

if ($lua -match 'Dual Equirectangular \(Horiz\)') {
    Write-Host 'lua: projection names already patched'
} elseif ($lua -match '(?m)^\s*\[7\] = "Dual Equi-Angular Cubemap",\s*$') {
    $lua = $lua -replace '(?m)^(\s*)\[7\] = "Dual Equi-Angular Cubemap",\s*$',
                         "`$1[7] = `"Dual Equi-Angular Cubemap`",`n`$1[8] = `"Dual Equirectangular (Horiz)`",`n`$1[9] = `"Fisheye`","
    $patched = $true
} else {
    Write-Warning 'lua: projection_names table not found — upstream changed. Ctrl+Shift+p will not reach projections 8/9 (the OSD menu still will).'
}

# is_dual_eye() drives the "Eye: ..." label; 8 is a stereo layout too.
if ($lua -notmatch 'config\.input_projection == 8') {
    if ($lua -match '(?m)^(\s*)config\.input_projection == 7\s*$') {
        $lua = $lua -replace '(?m)^(\s*)config\.input_projection == 7\s*$',
                             "`$1config.input_projection == 7 or`n`$1config.input_projection == 8"
        $patched = $true
    } else {
        Write-Warning 'lua: is_dual_eye() not found — the eye label may be wrong for projection 8.'
    }
}

if ($patched) {
    Write-Utf8 $luaPath $lua
    Write-Host 'lua: patched for projections 8 (360 SBS) and 9 (mono fisheye)'
}

# -------------------------------------------------------------------- uosc ---
# The control bar. mpv's built-in OSC has a hard-coded layout with no way to add
# a button, which is the whole reason for replacing it: the VR mode switcher
# needs to live in the bar, not behind a hotkey.
#
# Only the code is fetched — script-opts\uosc.conf is ours (it defines the
# controls row including our button) and is never overwritten.

$uoscMarker = Join-Path $mpvDir 'scripts\uosc\main.lua'
if ((Test-Path $uoscMarker) -and -not $Force) {
    Write-Host 'skip (exists): uosc'
} else {
    $zip = Join-Path $env:TEMP 'uosc-install.zip'
    Write-Host 'get  uosc (latest release)'
    Invoke-WebRequest -Uri 'https://github.com/tomasklaen/uosc/releases/latest/download/uosc.zip' `
                      -OutFile $zip -UseBasicParsing

    # Expand-Archive refuses to merge into a populated directory, so stage and copy.
    $stage = Join-Path $env:TEMP 'uosc-stage'
    if (Test-Path $stage) { Remove-Item $stage -Recurse -Force }
    Expand-Archive -Path $zip -DestinationPath $stage -Force

    foreach ($sub in @('scripts', 'fonts')) {
        $from = Join-Path $stage $sub
        if (Test-Path $from) {
            Copy-Item $from -Destination $mpvDir -Recurse -Force
        }
    }
    Remove-Item $stage -Recurse -Force
    Remove-Item $zip -Force
    Write-Host 'uosc: installed (scripts\uosc, fonts)'
}

# Our own uosc translations, copied in after the release is unpacked.
#
# uosc ships no Japanese or Korean, and scripts\uosc\ is gitignored and replaced
# wholesale by the block above -- so these cannot live there. They are kept in
# mpv\uosc-intl (tracked) and copied in on every run, including the "skip
# (exists)" path, so adding a language does not require -Force.
#
# Without them a Japanese user gets a Japanese menu bar over an English control
# bar, which is the half-translated state this is all meant to end.
$intlSrc = Join-Path $repoRoot 'mpv\uosc-intl'
$intlDst = Join-Path $mpvDir 'scripts\uosc\intl'
if ((Test-Path $intlSrc) -and (Test-Path $intlDst)) {
    $copied = @()
    foreach ($f in Get-ChildItem $intlSrc -Filter *.json) {
        Copy-Item $f.FullName -Destination $intlDst -Force
        $copied += $f.BaseName
    }
    if ($copied.Count -gt 0) { Write-Host "uosc: added our locales ($($copied -join ', '))" }
}

# mpv360 patch: load the shader through the argument-array form of the command.
#
# Upstream builds the command as one string:
#
#     mp.command("no-osd change-list glsl-shaders append " .. config.shader_path)
#
# mpv splits a string command on whitespace, so any config directory containing
# a space truncates the path and the shader silently never loads. The symptom is
# total: glsl-shaders stays empty, every VR mode looks like plain 2D, and the
# mode switches appear to do nothing because the state changes but nothing
# renders. Our own release folder ("VR Flat Player 0.1") hits it, and so does
# any user whose Windows account name has a space in it.
#
# mp.commandv passes the arguments separately and never splits them.
$m360 = Join-Path $mpvDir 'scripts\mpv360.lua'
if (Test-Path $m360) {
    $lua = Read-Utf8 $m360
    if ($lua -match 'commandv\("no-osd", "change-list", "glsl-shaders"') {
        Write-Host 'mpv360: shader-path patch already applied'
    } elseif ($lua -match 'mp\.command\("no-osd change-list glsl-shaders (append|remove) " \.\. config\.shader_path\)') {
        $lua = $lua -replace `
            'mp\.command\("no-osd change-list glsl-shaders (append|remove) " \.\. config\.shader_path\)', `
            'mp.commandv("no-osd", "change-list", "glsl-shaders", "$1", config.shader_path)'
        Write-Utf8 $m360 $lua
        Write-Host 'mpv360: patched shader loading to survive spaces in the path'
    } else {
        Write-Warning 'mpv360: shader-load call not found — upstream changed. VR modes may not render.'
    }
}

# uosc is installed unpatched, on purpose.
#
# An earlier version of this script rewrote lib/utils.lua to make uosc's
# "forward unhandled clicks to other scripts" path case-insensitive, in the hope
# of getting drag-to-look through uosc's forced MBTN_LEFT binding. It did not
# work (the forward never arrived either way) and drag detection now happens in
# the bridge at the Win32 level, so the patch bought nothing.
#
# It also actively broke uosc: the patch rewrote lib/utils.lua through
# Get-Content/Set-Content, which mangled a U+2019 apostrophe in a string literal
# and made the whole script fail to load with "')' expected". If you ever do
# need to patch a uosc file, go through Read-Utf8/Write-Utf8 above.

Write-Host ''
Write-Host "installed into $mpvDir"
Write-Host 'Verify hardware decoding actually engaged (must NOT print "no"):'
Write-Host "  mpv --config-dir=`"$mpvDir`" --term-status-msg='`${hwdec-current}' <file>"
