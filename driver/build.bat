@echo off
rem Builds fbtcal\bin\win64\driver_fbtcal.dll and build\hooktest.exe.
rem Needs Visual Studio 2022 Build Tools with the C++ workload.
setlocal
set "ROOT=%~dp0"
set "VSWHERE=%ProgramFiles(x86)%\Microsoft Visual Studio\Installer\vswhere.exe"
for /f "usebackq delims=" %%i in (`"%VSWHERE%" -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath`) do set "VS=%%i"
if not defined VS (
    echo Visual Studio C++ Build Tools not found.
    exit /b 1
)
call "%VS%\VC\Auxiliary\Build\vcvars64.bat" >nul 2>nul || exit /b 1

set "MH=%ROOT%..\third_party\minhook"
set "OVR=%ROOT%..\third_party\openvr\headers"
set "OUT=%ROOT%fbtcal\bin\win64"
set "OBJ=%ROOT%build"
if not exist "%OUT%" mkdir "%OUT%"
if not exist "%OBJ%" mkdir "%OBJ%"

set "COMMON=/nologo /O2 /MT /W3 /DWIN32_LEAN_AND_MEAN /DNOMINMAX /I"%MH%\include" /I"%MH%\src" /I"%OVR%" /I"%ROOT%src""

cl %COMMON% /c /Fo"%OBJ%/" "%MH%\src\buffer.c" "%MH%\src\hook.c" "%MH%\src\trampoline.c" "%MH%\src\hde\hde64.c" || exit /b 1
cl %COMMON% /std:c++17 /EHsc /LD /Fo"%OBJ%/" /Fe"%OUT%\driver_fbtcal.dll" "%ROOT%src\driver_main.cpp" "%OBJ%\buffer.obj" "%OBJ%\hook.obj" "%OBJ%\trampoline.obj" "%OBJ%\hde64.obj" /link /DLL || exit /b 1
cl %COMMON% /std:c++17 /EHsc /Fo"%OBJ%/" /Fe"%OBJ%\hooktest.exe" "%ROOT%tests\hooktest.cpp" || exit /b 1
echo Build OK: %OUT%\driver_fbtcal.dll
