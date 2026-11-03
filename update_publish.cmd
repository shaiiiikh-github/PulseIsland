@echo off
taskkill /IM PulseIsland.exe /F 2>nul
timeout /t 1 /nobreak >nul
if exist publish_new\PulseIsland.exe (
    copy /y publish_new\PulseIsland.exe publish\PulseIsland.exe >nul
    echo [OK] Updated publish\PulseIsland.exe successfully!
) else (
    echo [INFO] publish_new\PulseIsland.exe not found.
)

