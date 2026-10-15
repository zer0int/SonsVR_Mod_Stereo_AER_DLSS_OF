@echo off
setlocal EnableExtensions

rem SonsVR V90d native NVOFA typed-proxy bridge builder.
rem Robust to being launched either from native_nvof\ or from the V90 kit root.

for %%I in ("%~dp0.") do set "HERE=%%~fI"

if exist "%HERE%\SonsVR_NvOF_V90.cpp" (
    set "ROOT=%HERE%"
) else if exist "%HERE%\native_nvof\SonsVR_NvOF_V90.cpp" (
    set "ROOT=%HERE%\native_nvof"
) else (
    echo ERROR: cannot locate SonsVR_NvOF_V90.cpp.
    echo Checked:
    echo   %HERE%\SonsVR_NvOF_V90.cpp
    echo   %HERE%\native_nvof\SonsVR_NvOF_V90.cpp
    exit /b 10
)

set "OUT=%ROOT%\build"
set "BASE=%ROOT%\Common\NvOFBase"
set "IFACE=%ROOT%\NvOFInterface"
set "SRC_MAIN=%ROOT%\SonsVR_NvOF_V90.cpp"
set "SRC_BASE=%BASE%\NvOF.cpp"
set "SRC_D3D11=%BASE%\NvOFD3D11.cpp"
set "SRC_D3DCOMMON=%BASE%\NvOFD3DCommon.cpp"
set "DLL=%OUT%\SonsVR_NvOF_V90.dll"
set "PDB=%OUT%\SonsVR_NvOF_V90.pdb"

if not exist "%OUT%" mkdir "%OUT%"

for %%F in ("%SRC_MAIN%" "%SRC_BASE%" "%SRC_D3D11%" "%SRC_D3DCOMMON%") do (
    if not exist "%%~fF" (
        echo ERROR: required native source is missing:
        echo   %%~fF
        exit /b 11
    )
)

set "VSWHERE=%ProgramFiles(x86)%\Microsoft Visual Studio\Installer\vswhere.exe"
if not exist "%VSWHERE%" (
    echo ERROR: vswhere.exe not found. Visual Studio C++ Build Tools are required.
    exit /b 2
)

set "VSINSTALL="
for /f "usebackq tokens=*" %%I in (`"%VSWHERE%" -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath`) do set "VSINSTALL=%%I"
if not defined VSINSTALL (
    echo ERROR: no Visual Studio installation with x64 C++ tools was found.
    exit /b 3
)

call "%VSINSTALL%\VC\Auxiliary\Build\vcvars64.bat" >nul
set "VCVARS_RC=%ERRORLEVEL%"
if not "%VCVARS_RC%"=="0" exit /b %VCVARS_RC%

if exist "%DLL%" del /q "%DLL%"
if exist "%PDB%" del /q "%PDB%"

echo [native] Root: %ROOT%
echo [native] Sources verified:
echo   %SRC_MAIN%
echo   %SRC_BASE%
echo   %SRC_D3D11%
echo   %SRC_D3DCOMMON%
echo [native] Building SonsVR_NvOF_V90.dll against NVIDIA Optical Flow SDK 5.0.7 sources...

rem ROOT is canonicalized above and deliberately has NO trailing backslash.
rem Keep the cl invocation on one physical line.
cl /nologo /std:c++17 /O2 /EHsc /MD /LD /W3 /DWIN32 /D_WINDOWS /DNOMINMAX /I"%ROOT%" /I"%BASE%" /I"%IFACE%" "%SRC_MAIN%" "%SRC_BASE%" "%SRC_D3D11%" "%SRC_D3DCOMMON%" /link /NOLOGO d3d11.lib dxgi.lib /OUT:"%DLL%" /PDB:"%PDB%"
set "CL_RC=%ERRORLEVEL%"
if not "%CL_RC%"=="0" (
    echo ERROR: native NVOFA bridge build failed. cl/link exit code=%CL_RC%
    exit /b %CL_RC%
)

if not exist "%DLL%" (
    echo ERROR: cl.exe returned success but DLL is missing:
    echo   %DLL%
    exit /b 4
)

for %%F in ("%DLL%") do echo [native] OK: %%~fF  %%~zF bytes
exit /b 0
