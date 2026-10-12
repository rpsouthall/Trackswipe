using TrackSwipe.Overlay;
using Vortice.Direct2D1;
using Vortice.DirectComposition;
using Vortice.DXGI;
using Vortice.Mathematics;
using Size = System.Drawing.Size;
using BitmapInterpolationMode = Vortice.Direct2D1.BitmapInterpolationMode;

namespace TrackSwipe.Interop;

/// <summary>Copies the composed desktop into a GPU surface without mapping pixels to CPU memory.</summary>
internal sealed class GpuCapture : IDisposable
{
    private readonly GpuDevice _gpu;
    private readonly Dictionary<Rectangle, IDXGIOutputDuplication> _outputs = new();
    private readonly HashSet<Rectangle> _reported = new();
    public GpuCapture(GpuDevice gpu) => _gpu = gpu;

    public bool Prepare(Rectangle monitor)
    {
        if (_outputs.ContainsKey(monitor)) return true;
        try
        {
            using var dxgi = _gpu.Device.QueryInterface<IDXGIDevice>();
            using var adapter = dxgi.GetAdapter();
            for (uint index = 0; adapter.EnumOutputs(index, out var output).Success; index++)
            {
                using (output)
                {
                    var bounds = output.Description.DesktopCoordinates;
                    if (Rectangle.FromLTRB(bounds.Left, bounds.Top, bounds.Right, bounds.Bottom) != monitor) continue;
                    using var output1 = output.QueryInterface<IDXGIOutput1>();
                    _outputs[monitor] = output1.DuplicateOutput(_gpu.Device);
                    return true;
                }
            }
            Report(monitor, "monitor is not attached to the rendering adapter");
        }
        catch (SharpGen.Runtime.SharpGenException ex) { Report(monitor, ex.Message); }
        return false;
    }

    public IDCompositionSurface? Copy(Rectangle monitor, Rectangle region)
    {
        if (!Prepare(monitor)) return null;
        var duplication = _outputs[monitor];
        var result = duplication.AcquireNextFrame(16, out _, out var resource);
        if (result.Failure)
        {
            Report(monitor, $"frame acquisition returned 0x{result.Code:X8}");
            if (result.Code != unchecked((int)0x887A0027) /* DXGI_ERROR_WAIT_TIMEOUT */)
            {
                _outputs.Remove(monitor); duplication.Dispose();
            }
            return null;
        }
        IDCompositionSurface? surface = null;
        try
        {
            using (resource)
            using (var source = resource.QueryInterface<IDXGISurface>())
            {
                surface = Composition.Device.CreateSurface((uint)region.Width, (uint)region.Height, Format.B8G8R8A8_UNorm, AlphaMode.Premultiplied);
                using var context = surface.BeginDraw<ID2D1DeviceContext>(null, out var offset);
                try
                {
                    context.SetDpi(96, 96);
                    using var bitmap = context.CreateBitmapFromDxgiSurface(source,
                        new BitmapProperties1(new Vortice.DCommon.PixelFormat(Format.B8G8R8A8_UNorm, Vortice.DCommon.AlphaMode.Ignore), 96, 96));
                    var pixels = bitmap.PixelSize;
                    var rotation = duplication.Description.Rotation switch
                    {
                        ModeRotation.Rotate90 => 90, ModeRotation.Rotate180 => 180, ModeRotation.Rotate270 => 270, _ => 0,
                    };
                    context.Transform = GpuCaptureLayout.Transform(new Size(pixels.Width, pixels.Height), rotation,
                        new PointF(offset.X - region.X, offset.Y - region.Y));
                    context.Clear(new Color4(0, 0, 0, 0));
                    context.DrawBitmap(bitmap, 1, BitmapInterpolationMode.NearestNeighbor);
                }
                finally { surface.EndDraw().CheckError(); }
            }
            Composition.Changed();
            return surface;
        }
        catch (SharpGen.Runtime.SharpGenException ex)
        {
            surface?.Dispose(); Report(monitor, ex.Message); return null;
        }
        finally { duplication.ReleaseFrame().CheckError(); }
    }

    private void Report(Rectangle monitor, string reason)
    {
        if (_reported.Add(monitor)) Log.Write($"GPU restore cover unavailable: {reason}; retaining the live thumbnail and backing.");
    }

    public void Dispose()
    {
        foreach (var output in _outputs.Values) output.Dispose();
        _outputs.Clear();
    }
}
