# Builds the shareable installer: release\fbtcalSetup.exe - one self-contained file (the .NET runtime
# inside, nothing to install first) that carries the SteamVR driver. Running it shows the installer;
# the installed copy is the same exe renamed fbtcal.exe (tray\src\Installer.cs).
# Close SteamVR first if it runs with this folder's driver: the driver DLL is rebuilt.
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot

& cmd /c "`"$root\driver\build.bat`""
if ($LASTEXITCODE -ne 0) { throw 'driver build failed' }

$out = Join-Path $root 'release\build'
if (Test-Path $out) { Remove-Item -Recurse -Force $out }
dotnet publish (Join-Path $root 'tray\FbtCal.csproj') -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true `
    -p:FbtcalPayload=true -p:DebugType=none --nologo -o $out
if ($LASTEXITCODE -ne 0) { throw 'publish failed' }

$setup = Join-Path $root 'release\fbtcalSetup.exe'
Copy-Item -Force (Join-Path $out 'fbtcal.exe') $setup
Remove-Item -Recurse -Force $out
$size = [math]::Round((Get-Item $setup).Length / 1MB, 1)
$hash = (Get-FileHash $setup -Algorithm SHA256).Hash.ToLower()
Write-Output "release\fbtcalSetup.exe  ($size MB, sha256 $hash)"
