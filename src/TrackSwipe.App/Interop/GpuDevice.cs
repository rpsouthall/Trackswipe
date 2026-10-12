using Vortice.Direct2D1;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DirectWrite;
using Vortice.DXGI;
using FeatureLevel = Vortice.Direct3D.FeatureLevel;

namespace TrackSwipe.Interop;

/// <summary>One hardware Direct3D device shared by Direct2D drawing and GPU copies.</summary>
internal sealed class GpuDevice : IDisposable
{
    public ID3D11Device Device { get; }
    public ID3D11DeviceContext Context { get; }
    public ID2D1Factory1 Factory { get; }
    public ID2D1Device DrawingDevice { get; }
    public IDWriteFactory Text { get; }
    public GpuCapture Capture { get; }

    public GpuDevice()
    {
        D3D11.D3D11CreateDevice(null, DriverType.Hardware, DeviceCreationFlags.BgraSupport,
            new[] { FeatureLevel.Level_11_1, FeatureLevel.Level_11_0, FeatureLevel.Level_10_1 },
            out ID3D11Device device, out ID3D11DeviceContext context).CheckError();
        Device = device;
        Context = context;
        try
        {
            Factory = D2D1.D2D1CreateFactory<ID2D1Factory1>(Vortice.Direct2D1.FactoryType.SingleThreaded);
            using var dxgi = device.QueryInterface<IDXGIDevice>();
            DrawingDevice = Factory.CreateDevice(dxgi);
            Text = DWrite.DWriteCreateFactory<IDWriteFactory>();
            Capture = new GpuCapture(this);
        }
        catch { Context.Dispose(); Device.Dispose(); throw; }
    }

    public void Dispose()
    {
        Capture.Dispose(); Text.Dispose(); DrawingDevice.Dispose(); Factory.Dispose(); Context.Dispose(); Device.Dispose();
    }
}
