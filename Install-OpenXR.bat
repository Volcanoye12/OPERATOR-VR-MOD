@echo off
setlocal
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0install-openxr.ps1" -GameDir "%~dp0."
if errorlevel 1 (
    echo.
    echo OpenXR installation failed. Read the error above.
) else (
    echo.
    echo OpenXR is installed. You can now start OPERATOR in VR.
)
pause
