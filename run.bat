@echo off
cd /d "%~dp0"

if not exist smd_merge.exe (
    echo [ERROR] smd_merge.exe not found. Run build.bat first.
    pause
    exit /b 1
)

start "" smd_merge.exe
