@echo off
setlocal EnableExtensions DisableDelayedExpansion
rem SonsVR Diagnostic Collector v2 (Windows 10/11)
rem Run by double-clicking, or pass a game directory as the first argument.
rem Example: COLLECT_SONSVR_LOGS.bat "D:\SteamLibrary\steamapps\common\Sons Of The Forest"

if not exist "%~dp0COLLECT_SONSVR_LOGS.ps1" (
    echo ERROR: COLLECT_SONSVR_LOGS.ps1 must be next to this .bat file.
    pause
    exit /b 2
)

rem IMPORTANT: Pass the game path via environment rather than a quoted argument.
rem A quoted Windows directory argument ending in \ can corrupt PowerShell's
rem argv parsing (the exact V169C bug). Do not pass -OutputDirectory either.
set "SONSVR_COLLECT_GAME_DIR="
if not "%~1"=="" set "SONSVR_COLLECT_GAME_DIR=%~1"
if not defined SONSVR_COLLECT_GAME_DIR if defined SONS_GAME_DIR set "SONSVR_COLLECT_GAME_DIR=%SONS_GAME_DIR%"

powershell.exe -NoLogo -NoProfile -ExecutionPolicy Bypass -File "%~dp0COLLECT_SONSVR_LOGS.ps1"
set "RESULT=%ERRORLEVEL%"
if not "%RESULT%"=="0" echo ERROR: Diagnostic collection failed. See the message above.
if "%RESULT%"=="0" echo.
if "%RESULT%"=="0" echo ZIP saved next to COLLECT_SONSVR_LOGS.bat.
pause
exit /b %RESULT%
