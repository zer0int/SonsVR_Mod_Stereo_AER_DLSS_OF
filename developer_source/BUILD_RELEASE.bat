@echo off
setlocal EnableExtensions
set "ROOT=%~dp0"
if not "%~1"=="" set "SONS_GAME_DIR=%~1"
if not defined SONS_GAME_DIR (
    echo ERROR: Pass the game's folder as an argument or set SONS_GAME_DIR.
    echo Example: BUILD_RELEASE.bat "D:\SteamLibrary\steamapps\common\Sons Of The Forest"
    exit /b 2
)
if not exist "%SONS_GAME_DIR%\MelonLoader\Il2CppAssemblies" (
    echo ERROR: IL2CPP interop assemblies not found under: %SONS_GAME_DIR%
    echo Install/setup MelonLoader and launch the game at least once.
    exit /b 3
)
if not defined UNITY_EXE set "UNITY_EXE=C:\Program Files\Unity 2022.2.16f1\Editor\Unity.exe"
if not exist "%UNITY_EXE%" (
    echo ERROR: Unity 2022.2.16f1 executable not found: %UNITY_EXE%
    echo Set UNITY_EXE to your Editor executable before running.
    exit /b 4
)
set "UNITY_PROJECT=%ROOT%shader_project"
set "BUNDLE=%UNITY_PROJECT%\THINGY_OUT\sonsvr_depththingy"
set "RESOURCE=%ROOT%mod\Resources\sonsvr_depththingy"
set "NATIVE_DLL=%ROOT%native_nvof\build\SonsVR_NvOF_V90.dll"
set "OUT_DIR=%ROOT%OUTPUT"
set "LABEL=community_v169l"
set "MANAGED_DLL=%ROOT%mod\builds\%LABEL%\SonsVR_Mod.dll"

echo [1/3] Native NVIDIA optical-flow bridge...
call "%ROOT%native_nvof\BUILD_NATIVE_V90.bat"
if errorlevel 1 exit /b 5
if not exist "%NATIVE_DLL%" exit /b 6

echo [2/3] Unity shader AssetBundle...
if exist "%BUNDLE%" del /q "%BUNDLE%"
if exist "%RESOURCE%" del /q "%RESOURCE%"
if not exist "%ROOT%mod\Resources" mkdir "%ROOT%mod\Resources"
"%UNITY_EXE%" -batchmode -quit -projectPath "%UNITY_PROJECT%" -executeMethod BuildDepthThingy.BuildBatch -logFile "%ROOT%shader_build.log"
if errorlevel 1 exit /b 7
if not exist "%BUNDLE%" (echo ERROR: shader bundle not produced. & exit /b 8)
copy /y "%BUNDLE%" "%RESOURCE%" >nul
if errorlevel 1 exit /b 9

echo [3/3] Managed VR mod...
call "%ROOT%mod\build_named.bat" "%LABEL%"
if errorlevel 1 exit /b 10
if not exist "%MANAGED_DLL%" exit /b 11
if not exist "%OUT_DIR%" mkdir "%OUT_DIR%"
copy /y "%MANAGED_DLL%" "%OUT_DIR%\SonsVR_Mod.dll" >nul
if errorlevel 1 exit /b 12
copy /y "%NATIVE_DLL%" "%OUT_DIR%\SonsVR_NvOF_V90.dll" >nul
if errorlevel 1 exit /b 13
echo SUCCESS: install both DLLs from OUTPUT into the game's Mods folder.
exit /b 0
