@echo off
setlocal EnableExtensions
if "%~1"=="" (
    echo Usage: build_named.bat BUILD_LABEL
    exit /b 2
)
if not defined SONS_GAME_DIR (
    echo ERROR: Set SONS_GAME_DIR to the Sons of the Forest installation path.
    exit /b 2
)
dotnet build "%~dp0SonsVR_Mod.csproj" -c Release "-p:BuildLabel=%~1" "-p:SonsGameDir=%SONS_GAME_DIR%"
exit /b %errorlevel%
