@echo off
REM ---------------------------------------------------------------------------
REM  VR Flat Player - build from source and run it.
REM
REM  Drag a video file onto this .bat, or:
REM      run.bat "E:\vr\clip8k.mp4"
REM      run.bat --source=udp "D:\clips\test4k.mp4"
REM      run.bat --detached "E:\vr\clip8k.mp4"
REM
REM  This is the developer path. A released copy needs none of this - it is
REM  just VRFlatPlayer.exe, and you double-click that.
REM
REM  Deliberately ASCII-only; see the note in publish.bat.
REM ---------------------------------------------------------------------------
setlocal
pushd "%~dp0"

dotnet run --project "src\HeadTrackBridge" -c Release -- %*
set "ERR=%ERRORLEVEL%"

popd

echo.
if not "%ERR%"=="0" echo *** Exited with code %ERR%.

REM Always pause: the player is a windowed app, so without this the console
REM closes the instant it quits and takes every diagnostic line with it.
pause
exit /b %ERR%
