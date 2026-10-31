@echo off
echo Stopping Jerome...
taskkill /F /IM JeromeSharp.exe /T >nul 2>&1
if %errorlevel%==0 (
    echo Jerome stopped.
) else (
    echo Jerome wasn't running.
)
pause
