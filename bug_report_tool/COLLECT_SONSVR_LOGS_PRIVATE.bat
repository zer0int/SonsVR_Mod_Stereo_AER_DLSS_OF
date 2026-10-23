@echo off
setlocal EnableExtensions DisableDelayedExpansion
rem SonsVR Collector V3: privacy-reduced, non-crash bug report.
rem Collects only filtered SonsVR lines from current Latest.log and at most
rem three limited F9 camera/volume snapshots. No Player.log or hardware dump.
rem Example: COLLECT_SONSVR_LOGS_PRIVATE.bat "D:\SteamLibrary\steamapps\common\Sons Of The Forest"

if not exist "%~dp0COLLECT_SONSVR_LOGS.ps1" (
    echo ERROR: Keep COLLECT_SONSVR_LOGS.ps1 next to this .bat file.
    pause
    exit /b 2
)

rem Pass game-directory paths via the environment to preserve trailing backslashes,
rem spaces, parentheses and shell-special filename characters.
set "SONSVR_COLLECT_GAME_DIR="
if not "%~1"=="" set "SONSVR_COLLECT_GAME_DIR=%~1"
if not defined SONSVR_COLLECT_GAME_DIR if defined SONS_GAME_DIR set "SONSVR_COLLECT_GAME_DIR=%SONS_GAME_DIR%"

echo SonsVR PRIVACY-REDUCED diagnostic collector
echo Excludes Player.log, hardware details, old logs, and raw UI text.
echo The resulting ZIP may still contain gameplay coordinates or object names.
echo.
powershell.exe -NoLogo -NoProfile -ExecutionPolicy Bypass -File "%~dp0COLLECT_SONSVR_LOGS.ps1" -PrivacyReduced
set "RESULT=%ERRORLEVEL%"
if not "%RESULT%"=="0" echo ERROR: Diagnostic collection failed. See the message above.
if "%RESULT%"=="0" echo.
if "%RESULT%"=="0" echo Private ZIP saved next to this .BAT file.
pause
exit /b %RESULT%
