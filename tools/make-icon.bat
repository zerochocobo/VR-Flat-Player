@echo off
REM Regenerates assets\icon.ico and the promo PNGs from the shapes in
REM tools\make-icon\Program.cs. Run it after changing the mark; the build
REM embeds whatever icon.ico happens to be there.

setlocal
cd /d "%~dp0.."
dotnet run --project tools\make-icon
if errorlevel 1 (
  echo.
  echo Icon generation failed.
  exit /b 1
)
endlocal
