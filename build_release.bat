@echo off
rem Builds the driver and the tray app into dist\fbtcal: fbtcal.exe + openvr_api.dll + fbtcal.vrmanifest
rem + driver\fbtcal\ (what SteamVR loads). Close SteamVR first: it locks the loaded driver DLL.
setlocal
set "ROOT=%~dp0"
set "DIST=%ROOT%dist\fbtcal"

call "%ROOT%driver\build.bat" || exit /b 1
dotnet publish "%ROOT%tray\FbtCal.csproj" -c Release -o "%DIST%" -p:PublishSingleFile=true -p:DebugType=none --nologo || exit /b 1

if not exist "%DIST%\driver\fbtcal\bin\win64" mkdir "%DIST%\driver\fbtcal\bin\win64"
copy /y "%ROOT%driver\fbtcal\driver.vrdrivermanifest" "%DIST%\driver\fbtcal\" >nul || exit /b 1
copy /y "%ROOT%driver\fbtcal\bin\win64\driver_fbtcal.dll" "%DIST%\driver\fbtcal\bin\win64\" >nul || (
    echo Could not copy the driver DLL - is SteamVR running? Close it and build again.
    exit /b 1
)
echo Release: %DIST%\fbtcal.exe
