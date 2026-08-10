@echo off
REM ---------------------------------------------------------------------------
REM  VR Flat Player - remove this player from the "Open with" list.
REM
REM  Undoes register-file-types.bat. Only touches HKEY_CURRENT_USER keys that
REM  the register script created; your other players are left alone.
REM
REM  Deliberately ASCII-only, same reason as the other .bat files here.
REM ---------------------------------------------------------------------------
setlocal
pushd "%~dp0"

set "PS1=tools\register-file-association.ps1"
if not exist "%PS1%" set "PS1=register-file-association.ps1"

if not exist "%PS1%" (
    echo *** Could not find register-file-association.ps1 next to this file.
    goto :done
)

powershell -NoProfile -ExecutionPolicy Bypass -File "%PS1%" -Unregister
set "ERR=%ERRORLEVEL%"

:done
popd
echo.
pause
exit /b %ERR%
