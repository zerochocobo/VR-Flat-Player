@echo off
REM ---------------------------------------------------------------------------
REM  VR Flat Player - add this player to the "Open with" list for video files.
REM
REM  Double-click this once. It writes only to HKEY_CURRENT_USER, so it needs
REM  no administrator rights and changes nothing for other users.
REM
REM  It does NOT take over as your default player. After running it, right-click
REM  a video, choose Open with, pick VR Flat Player, and tick "Always use this
REM  app" if that is what you want.
REM
REM  To undo:  unregister-file-types.bat
REM
REM  Deliberately ASCII-only. cmd.exe reads a .bat in the console's OEM code
REM  page, so non-ASCII text here renders as mojibake on most machines.
REM ---------------------------------------------------------------------------
setlocal
pushd "%~dp0"

REM The script sits under tools\ in the source tree, and next to this file in a
REM published copy.
set "PS1=tools\register-file-association.ps1"
if not exist "%PS1%" set "PS1=register-file-association.ps1"

if not exist "%PS1%" (
    echo *** Could not find register-file-association.ps1 next to this file.
    goto :done
)

powershell -NoProfile -ExecutionPolicy Bypass -File "%PS1%" %*
set "ERR=%ERRORLEVEL%"

if not "%ERR%"=="0" (
    echo.
    echo *** Registration FAILED, exit code %ERR%.
    echo *** If it cannot find VRFlatPlayer.exe, run publish.bat first.
)

:done
popd
echo.
pause
exit /b %ERR%
