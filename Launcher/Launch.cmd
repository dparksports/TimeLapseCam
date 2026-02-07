@echo off
setlocal
title TimeLapse Webcam Recorder - Setup
cd /d "%~dp0"

:: ===== Check Windows App SDK Runtime =====
set "NEED_APPRUNTIME=0"
reg query "HKLM\SOFTWARE\Microsoft\WinAppRuntime" >nul 2>&1
if %errorlevel% neq 0 (
    set "NEED_APPRUNTIME=1"
)

:: ===== Check VC++ Redistributable (2015-2022 x64) =====
set "NEED_VCREDIST=0"
reg query "HKLM\SOFTWARE\Microsoft\VisualStudio\14.0\VC\Runtimes\x64" >nul 2>&1
if %errorlevel% neq 0 (
    set "NEED_VCREDIST=1"
)

:: ===== Install if needed =====
if "%NEED_APPRUNTIME%"=="0" if "%NEED_VCREDIST%"=="0" goto :launch

echo ============================================
echo  TimeLapse Webcam Recorder - First Time Setup
echo ============================================
echo.

if "%NEED_APPRUNTIME%"=="1" (
    echo [1/2] Installing Windows App Runtime...
    echo       Downloading from Microsoft...
    curl -L -o "%TEMP%\WindowsAppRuntimeInstall.exe" "https://aka.ms/windowsappsdk/1.6/latest/windowsappruntimeinstall-x64.exe" >nul 2>&1
    if exist "%TEMP%\WindowsAppRuntimeInstall.exe" (
        echo       Installing... (this may take a minute)
        "%TEMP%\WindowsAppRuntimeInstall.exe" --quiet
        del "%TEMP%\WindowsAppRuntimeInstall.exe" >nul 2>&1
        echo       Done!
    ) else (
        echo       ERROR: Download failed. Please check your internet connection.
        echo       You can manually install from: https://learn.microsoft.com/en-us/windows/apps/windows-app-sdk/downloads
        pause
        exit /b 1
    )
) else (
    echo [1/2] Windows App Runtime... OK
)

if "%NEED_VCREDIST%"=="1" (
    echo [2/2] Installing VC++ Redistributable...
    echo       Downloading from Microsoft...
    curl -L -o "%TEMP%\vc_redist.x64.exe" "https://aka.ms/vs/17/release/vc_redist.x64.exe" >nul 2>&1
    if exist "%TEMP%\vc_redist.x64.exe" (
        echo       Installing...
        "%TEMP%\vc_redist.x64.exe" /quiet /norestart
        del "%TEMP%\vc_redist.x64.exe" >nul 2>&1
        echo       Done!
    ) else (
        echo       ERROR: Download failed. Please check your internet connection.
        pause
        exit /b 1
    )
) else (
    echo [2/2] VC++ Redistributable... OK
)

echo.
echo Setup complete! Launching app...
timeout /t 2 >nul

:launch
start "" "%~dp0TimeLapseCam.exe"
exit /b 0
