@echo off
setlocal
if not exist "%~dp0GwentVision\GwentVision.exe" (
    echo Please extract the complete Gwent Chronicle release ZIP first.
    pause
    exit /b 1
)
start "" /D "%~dp0GwentVision" "%~dp0GwentVision\GwentVision.exe"
endlocal
