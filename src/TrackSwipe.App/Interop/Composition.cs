using System.Runtime.InteropServices;
using Vortice.DirectComposition;

namespace TrackSwipe.Interop;

/// <summary>
/// DirectComposition, the API DWM itself composes with, holding live window thumbnails as visuals
/// (<c>DwmpCreateSharedThumbnailVisual</c>, undocumented, dwmapi ordinal 147). Visual changes are batched and
/// applied by DWM all at once on <see cref="Flush"/>: a frame's moves, fades, clips and restacking reach the
/// screen together or not at all, without one cross-process call per thumbnail.
/// </summary>
/// <remarks>
/// Called through raw COM vtables. The slot numbers follow dcomp.h's declaration order, with overloads in
/// reverse (as MSVC lays them out); each was verified on Windows 11 26200 by a standalone spike.
/// </remarks>
internal static unsafe class Composition
{
    private const int DeviceCommit = 3, DeviceCreateVisual = 6, DesktopDeviceCreateTargetForHwnd = 24, TargetSetRoot = 3;
    private const int SharedThumbnailOrdinal = 147;

    private static readonly Guid IID_IDCompositionDesktopDevice = new("5F4633FE-1E08-4CB8-8C75-CE24333F5602");

    private static IntPtr _device;
    public static IDCompositionDevice2 Device { get; private set; } = null!;
    public static GpuDevice Gpu { get; private set; } = null!;
    private static delegate* unmanaged[Stdcall]<IntPtr, IntPtr, uint, DWM_THUMBNAIL_PROPERTIES*, IntPtr, IntPtr*, IntPtr*, int> _createSharedThumbnail;
    private static FlushWindow? _flusher;
    private static bool _dirty;

    /// <summary>Visual property changes made since the process started, for frame statistics.</summary>
    public static long Changes;

    private static readonly bool CreateWith3D = Environment.GetEnvironmentVariable("TRACKSWIPE_EXPERIMENT_NO3D") != "1";

    /// <summary>Creates the device. Call once, on the UI thread; every visual must be used from that thread.</summary>
    public static void Initialize()
    {
        if (_device != IntPtr.Zero) return;
        var dwmapi = LoadLibraryW("dwmapi.dll");
        var create = GetProcAddress(dwmapi, SharedThumbnailOrdinal);
        if (create == IntPtr.Zero)
            throw new PlatformNotSupportedException("This version of Windows has no shared thumbnail visuals (dwmapi ordinal 147).");
        _createSharedThumbnail = (delegate* unmanaged[Stdcall]<IntPtr, IntPtr, uint, DWM_THUMBNAIL_PROPERTIES*, IntPtr, IntPtr*, IntPtr*, int>)create;
        var iid = IID_IDCompositionDesktopDevice;
        Gpu = new GpuDevice();
        Marshal.ThrowExceptionForHR(DCompositionCreateDevice3(Gpu.DrawingDevice.NativePointer, ref iid, out _device));
        Marshal.AddRef(_device);
        Device = new IDCompositionDevice2(_device);
        _flusher = new FlushWindow();
    }

    /// <summary>Makes <paramref name="root"/> the content of <paramref name="hwnd"/>, a window with no redirection bitmap.</summary>
    public static IntPtr CreateTarget(IntPtr hwnd, CompositionVisual root)
    {
        IntPtr target;
        Marshal.ThrowExceptionForHR(((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, int, IntPtr*, int>)Slot(_device, DesktopDeviceCreateTargetForHwnd))(_device, hwnd, 1, &target));
        Marshal.ThrowExceptionForHR(((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, int>)Slot(target, TargetSetRoot))(target, root.Pointer));
        return target;
    }

    public static CompositionVisual CreateVisual()
    {
        IntPtr visual;
        Marshal.ThrowExceptionForHR(((delegate* unmanaged[Stdcall]<IntPtr, IntPtr*, int>)Slot(_device, DeviceCreateVisual))(_device, &visual));
        return new CompositionVisual(visual);
    }

    /// <summary>
    /// A visual showing <paramref name="source"/> live, drawn by DWM at <paramref name="size"/>. False if DWM
    /// refuses (the window closed). <paramref name="thumbnail"/> is a DWM thumbnail id: resize it with
    /// <c>DwmUpdateThumbnailProperties</c>, free it with <c>DwmUnregisterThumbnail</c> after releasing the visual.
    /// </summary>
    public static bool TryCreateThumbnail(IntPtr host, IntPtr source, out CompositionVisual? visual, out IntPtr thumbnail)
    {
        // No source rectangle: DWM's default is exactly the visible window, without its invisible resize borders.
        var properties = new DWM_THUMBNAIL_PROPERTIES
        {
            Flags = NativeMethods.DWM_TNP_RECTDESTINATION | NativeMethods.DWM_TNP_VISIBLE | NativeMethods.DWM_TNP_OPACITY |
                NativeMethods.DWM_TNP_SOURCECLIENTAREAONLY | (CreateWith3D ? NativeMethods.DWM_TNP_ENABLE3D : 0),
            Destination = new RECT { Right = 1, Bottom = 1 },
            Opacity = 255,
            Visible = 1,
        };
        IntPtr pointer, id;
        if (_createSharedThumbnail(host, source, 2, &properties, _device, &pointer, &id) < 0 || pointer == IntPtr.Zero)
        {
            visual = null;
            thumbnail = IntPtr.Zero;
            return false;
        }
        visual = new CompositionVisual(pointer);
        thumbnail = id;
        return true;
    }

    /// <summary>Records that visuals changed. They're committed at the end of the frame or, outside frames, of the current message.</summary>
    public static void Changed()
    {
        Changes++;
        if (_dirty) return;
        _dirty = true;
        _flusher?.Schedule();
    }

    /// <summary>Sends every change since the last flush to DWM as one batch.</summary>
    public static void Flush()
    {
        if (!_dirty) return;
        _dirty = false;
        var hr = ((delegate* unmanaged[Stdcall]<IntPtr, int>)Slot(_device, DeviceCommit))(_device);
        if (hr < 0) Log.Write($"DirectComposition commit failed: 0x{hr:X8}");
    }

    internal static IntPtr Slot(IntPtr comObject, int index) => (*(IntPtr**)comObject)[index];

    public static void Dispose()
    {
        if (_device == IntPtr.Zero) return;
        Flush();
        Device.Dispose();
        Marshal.Release(_device);
        _device = IntPtr.Zero;
        Gpu.Dispose();
    }

    /// <summary>A message-only window that flushes once the current message has been handled, batching everything it did.</summary>
    private sealed class FlushWindow : NativeWindow
    {
        private const int WM_FLUSH = NativeMethods.WM_APP + 7;
        private bool _scheduled;

        public FlushWindow() => CreateHandle(new CreateParams { Caption = "TrackSwipe composition", Parent = NativeMethods.HWND_MESSAGE });

        public void Schedule()
        {
            if (_scheduled) return;
            _scheduled = true;
            NativeMethods.PostMessageW(Handle, WM_FLUSH, IntPtr.Zero, IntPtr.Zero);
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WM_FLUSH)
            {
                _scheduled = false;
                Flush();
                return;
            }
            base.WndProc(ref m);
        }
    }

    [DllImport("dcomp.dll")]
    private static extern int DCompositionCreateDevice3(IntPtr renderingDevice, ref Guid iid, out IntPtr device);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr LoadLibraryW(string name);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetProcAddress(IntPtr module, nint ordinal);
}

/// <summary>An IDCompositionVisual. Changes take effect on the next <see cref="Composition.Flush"/>.</summary>
internal sealed unsafe class CompositionVisual
{
    private const int SetTransformMatrix = 8, SetClipObject = 13, SetClipRect = 14, AddVisualSlot = 16, RemoveVisualSlot = 17;
    private const int Visual2SetOpacityMode = 20, Visual3SetOpacity = 30, Visual3SetVisible = 33;
    private static readonly Guid IID_IDCompositionVisual3 = new("2775F462-B6C1-4015-B0BE-B3E7D6A4976D");

    private IntPtr _visual3;

    internal CompositionVisual(IntPtr pointer) => Pointer = pointer;

    public IDCompositionVisual Borrow()
    {
        Marshal.AddRef(Pointer);
        return new IDCompositionVisual(Pointer);
    }

    public void SetTransform(IDCompositionTransform transform)
    {
        using var visual = Borrow();
        visual.SetTransform(transform).CheckError();
        Composition.Changed();
    }

    public void SetClip(IDCompositionRectangleClip clip)
    {
        using var visual = Borrow();
        visual.SetClip(clip).CheckError();
        Composition.Changed();
    }

    public void AnimateOpacity(IDCompositionAnimation animation)
    {
        using var visual = Borrow();
        using var visual3 = visual.QueryInterface<IDCompositionVisual3>();
        visual3.SetOpacity(animation).CheckError();
        Composition.Changed();
    }

    public IntPtr Pointer { get; private set; }

    private IntPtr Visual3
    {
        get
        {
            if (_visual3 == IntPtr.Zero)
            {
                var iid = IID_IDCompositionVisual3;
                Marshal.ThrowExceptionForHR(Marshal.QueryInterface(Pointer, ref iid, out _visual3));
            }
            return _visual3;
        }
    }

    /// <summary>Positions the visual's origin at (<paramref name="x"/>, <paramref name="y"/>) in its parent.</summary>
    public void SetTranslation(float x, float y)
    {
        var matrix = stackalloc float[6] { 1, 0, 0, 1, x, y };
        Check(((delegate* unmanaged[Stdcall]<IntPtr, float*, int>)Composition.Slot(Pointer, SetTransformMatrix))(Pointer, matrix));
    }

    /// <summary>Scales the content inside a separately positioned and clipped container.</summary>
    public void SetScale(float x, float y)
    {
        var matrix = stackalloc float[6] { x, 0, 0, y, 0, 0 };
        Check(((delegate* unmanaged[Stdcall]<IntPtr, float*, int>)Composition.Slot(Pointer, SetTransformMatrix))(Pointer, matrix));
    }

    public void SetMatrix(float scaleX, float scaleY, float x, float y)
    {
        var matrix = stackalloc float[6] { scaleX, 0, 0, scaleY, x, y };
        Check(((delegate* unmanaged[Stdcall]<IntPtr, float*, int>)Composition.Slot(Pointer, SetTransformMatrix))(Pointer, matrix));
    }

    /// <summary>Clips to <paramref name="rect"/>, in the visual's own coordinates (before its translation).</summary>
    public void SetClip(RectangleF rect)
    {
        var r = stackalloc float[4] { rect.Left, rect.Top, rect.Right, rect.Bottom };
        Check(((delegate* unmanaged[Stdcall]<IntPtr, float*, int>)Composition.Slot(Pointer, SetClipRect))(Pointer, r));
    }

    public void ClearClip() =>
        Check(((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, int>)Composition.Slot(Pointer, SetClipObject))(Pointer, IntPtr.Zero));

    /// <summary>
    /// Applies opacity to each child directly instead of to the composed group. The default renders the
    /// group into an intermediate texture every frame first; for a visual with one child the result is identical.
    /// </summary>
    public void UseMultipliedOpacity() =>
        Check(((delegate* unmanaged[Stdcall]<IntPtr, int, int>)Composition.Slot(Visual3, Visual2SetOpacityMode))(Visual3, 1 /* DCOMPOSITION_OPACITY_MODE_MULTIPLY */));

    public void SetOpacity(float opacity) =>
        Check(((delegate* unmanaged[Stdcall]<IntPtr, float, int>)Composition.Slot(Visual3, Visual3SetOpacity))(Visual3, opacity));

    public void SetVisible(bool visible) =>
        Check(((delegate* unmanaged[Stdcall]<IntPtr, int, int>)Composition.Slot(Visual3, Visual3SetVisible))(Visual3, visible ? 1 : 0));

    /// <summary>Adds a child above all others, or below all others.</summary>
    public void Add(CompositionVisual child, bool onTop = true) =>
        // DirectComposition's insertAbove with no reference visual: FALSE adds in front of every sibling, TRUE behind (verified).
        Check(((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, int, IntPtr, int>)Composition.Slot(Pointer, AddVisualSlot))(Pointer, child.Pointer, onTop ? 0 : 1, IntPtr.Zero));

    public void Remove(CompositionVisual child) =>
        Check(((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, int>)Composition.Slot(Pointer, RemoveVisualSlot))(Pointer, child.Pointer));

    public void Release()
    {
        if (_visual3 != IntPtr.Zero) Marshal.Release(_visual3);
        if (Pointer != IntPtr.Zero) Marshal.Release(Pointer);
        _visual3 = Pointer = IntPtr.Zero;
    }

    private static void Check(int hr)
    {
        Composition.Changed();
        if (hr < 0) Marshal.ThrowExceptionForHR(hr);
    }
}
