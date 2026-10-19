@echo off
setlocal

set "SCRIPT_DIR=%~dp0"
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%SCRIPT_DIR%Start-LocalCoopClients.ps1" -ClientCount 3
set "LOCALCOOP_EXIT_CODE=%ERRORLEVEL%"

if not "%LOCALCOOP_EXIT_CODE%"=="0" (
    echo.
    echo LocalCoop launcher exited with code %LOCALCOOP_EXIT_CODE%.
    pause
)

exit /b %LOCALCOOP_EXIT_CODE%
