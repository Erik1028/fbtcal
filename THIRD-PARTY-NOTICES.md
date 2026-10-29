# Third-party notices

fbtcal includes or links the following. Their full license texts are in the files named here.

| Part | Used for | License |
|---|---|---|
| [MinHook](https://github.com/TsudaKageyu/minhook), Copyright (C) 2009-2017 Tsuda Kageyu | the driver's hook on SteamVR's pose updates (`third_party/minhook`, compiled into `driver_fbtcal.dll`) | BSD 2-Clause, [third_party/minhook/LICENSE.txt](third_party/minhook/LICENSE.txt) |
| [OpenVR SDK](https://github.com/ValveSoftware/openvr), Copyright (c) 2015 Valve Corporation | `openvr_driver.h`, the C# binding `openvr_api.cs` and `openvr_api.dll` (`third_party/openvr`) | BSD 3-Clause, [third_party/openvr/LICENSE](third_party/openvr/LICENSE) |
| [Vortice.Windows](https://github.com/amerkoleci/Vortice.Windows) (Vortice.Direct3D11, Vortice.DXGI, Vortice.DirectX), Copyright (c) Amer Koleci and Contributors | the dashboard tab's GPU texture | MIT |
| [SharpGen.Runtime](https://github.com/SharpGenTools/SharpGenTools), (c) 2010-2017 Alexandre Mutel, 2017-2023 Jeremy Koritzinsky, 2023-2024 Amer Koleci | used by Vortice | MIT |
| [.NET](https://github.com/dotnet/runtime) runtime and Windows Forms, Copyright (c) .NET Foundation and Contributors | the app; `fbtcalSetup.exe` carries the runtime | MIT |

fbtcal reads, but does not include, files of [SlimeVR](https://github.com/SlimeVR/SlimeVR-Server)
(the body proportions in `%APPDATA%\dev.slimevr.SlimeVR`). The SlimeVR tracker drawn in fbtcal's icon is
an illustration of the hardware fbtcal is made for. fbtcal is not affiliated with SlimeVR, VRChat or Valve.
