@echo off
REM ---------------------------------------------------------------------------
REM  VR Flat Player - build a distributable copy into dist\
REM
REM  Double-click this, or pass options straight through to the script:
REM      publish.bat -FrameworkDependent
REM      publish.bat -NoZip
REM      publish.bat -MpvExe "C:\path\to\mpv.exe"
REM
REM  Deliberately ASCII-only. cmd.exe reads a .bat in the console's OEM code
REM  page, so non-ASCII text here renders as mojibake on most machines; the
REM  Chinese documentation lives in README.md instead.
REM ---------------------------------------------------------------------------
setlocal
pushd "%~dp0"

powershell -NoProfile -ExecutionPolicy Bypass -File "tools\publish.ps1" %*
set "ERR=%ERRORLEVEL%"

popd

if not "%ERR%"=="0" (
    echo.
    echo *** Publish FAILED, exit code %ERR%.
    echo *** If it says mpv\scripts\uosc is missing, run this first:
    echo ***     powershell -ExecutionPolicy Bypass -File tools\install-mpv360.ps1
)

echo.
pause
exit /b %ERR%
