@echo off
REM ---------------------------------------------------------------------------
REM  VR Flat Player - download the face detector for webcam head tracking.
REM
REM  About 230 KB. Only needed if you want head tracking; the player works
REM  without it on mouse drag alone.
REM
REM  Deliberately ASCII-only, same reason as the other .bat files here.
REM ---------------------------------------------------------------------------
setlocal
pushd "%~dp0.."

powershell -NoProfile -ExecutionPolicy Bypass -File "tools\install-models.ps1" %*
set "ERR=%ERRORLEVEL%"

popd
echo.
pause
exit /b %ERR%
