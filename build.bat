@echo off
setlocal
cd /d "%~dp0"

set "CSC="
if exist "%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe" set "CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
if not defined CSC if exist "%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe" set "CSC=%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe"

if not defined CSC (
    echo [ERROR] csc.exe not found. Enable .NET Framework 4.x in Windows Features, then retry.
    pause
    exit /b 1
)

echo Compiling with: %CSC%
"%CSC%" /nologo /target:winexe /platform:anycpu /optimize+ /out:smd_merge.exe /r:System.Windows.Forms.dll /r:System.Drawing.dll src\smd_merge_gui.cs src\sleeve_retarget.cs
if errorlevel 1 (
    echo [ERROR] compile failed. See messages above.
    pause
    exit /b 1
)

echo.
echo SUCCESS: smd_merge.exe created in this folder.
pause
