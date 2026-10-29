using System;
using Valve.VR;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace FbtCal
{
    // One D3D11 texture on the headset's GPU that the panel's picture is copied into, handed to SteamVR
    // with SetOverlayTexture (like Orbit's overlays). SetOverlayRaw made SteamVR create a new texture on
    // every redraw, and the panel blinked in the headset whenever it changed (10-29).
    sealed class PanelTexture : IDisposable
    {
        readonly ID3D11Device _device;
        readonly ID3D11DeviceContext _ctx;
        readonly ID3D11Texture2D _tex;

        // adapterIndex: IVRSystem.GetDXGIOutputInfo's (-1 = the default GPU)
        public PanelTexture(int adapterIndex, int w, int h)
        {
            using var dxgi = DXGI.CreateDXGIFactory1<IDXGIFactory1>();
            IDXGIAdapter1 adapter = null;
            if (adapterIndex >= 0 && dxgi.EnumAdapters1((uint)adapterIndex, out var a).Success) adapter = a;
            try
            {
                var levels = new[] { FeatureLevel.Level_11_1, FeatureLevel.Level_11_0 };
                D3D11.D3D11CreateDevice(adapter, adapter == null ? DriverType.Hardware : DriverType.Unknown,
                                        DeviceCreationFlags.BgraSupport, levels, out _device, out _ctx).CheckError();
            }
            finally { adapter?.Dispose(); }
            _tex = _device.CreateTexture2D(new Texture2DDescription
            {
                Width = (uint)w, Height = (uint)h, MipLevels = 1, ArraySize = 1,
                Format = Format.B8G8R8A8_UNorm, SampleDescription = new SampleDescription(1, 0),
                Usage = ResourceUsage.Default, BindFlags = BindFlags.ShaderResource | BindFlags.RenderTarget,
            });
        }

        // bgra: a GDI+ Format32bppArgb bitmap's pixels (B, G, R, A in memory), stride bytes a row.
        public EVROverlayError Upload(CVROverlay ov, ulong handle, IntPtr bgra, int stride)
        {
            _ctx.UpdateSubresource(_tex, 0, null, bgra, (uint)stride, 0);
            _ctx.Flush();
            var t = new Texture_t { handle = _tex.NativePointer, eType = ETextureType.DirectX, eColorSpace = EColorSpace.Auto };
            return ov.SetOverlayTexture(handle, ref t);
        }

        public void Dispose()
        {
            _tex?.Dispose();
            _ctx?.ClearState();
            _ctx?.Flush();
            _ctx?.Dispose();
            _device?.Dispose();
        }
    }
}
