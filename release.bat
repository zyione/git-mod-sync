@echo off
setlocal
cd /d "%~dp0"

echo.
echo ==========================================================
echo        ModSync 1-Click Release Publisher
echo ==========================================================
echo.

powershell -NoProfile -ExecutionPolicy Bypass -File ".\scripts\release.ps1" %*
if errorlevel 1 (
    echo.
    echo [ERROR] Release failed. See output above for details.
    echo.
    pause
    exit /b %errorlevel%
)

echo.
echo Release process finished successfully.
echo Press any key to exit...
pause >nul
