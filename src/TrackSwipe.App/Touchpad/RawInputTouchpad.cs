using System.ComponentModel;
using System.Runtime.InteropServices;
using TrackSwipe.Core;
using TrackSwipe.Interop;
using static TrackSwipe.Interop.NativeMethods;

namespace TrackSwipe.Touchpad;

/// <summary>
/// Receives precision touchpad reports through Raw Input, even while another app has focus.
/// Read-only: Windows keeps handling the touchpad as normal.
/// </summary>
internal sealed class RawInputTouchpad : NativeWindow, IDisposable
{
    private const ushort DigitizerPage = 0x0D, UsageTouchPad = 0x05;

    private readonly Dictionary<IntPtr, TouchpadDevice?> _devices = new();
    private readonly int _headerSize = Marshal.SizeOf<RAWINPUTHEADER>();
    private IntPtr _buffer;
    private int _bufferSize;

    public RawInputTouchpad()
    {
        CreateHandle(new CreateParams { Caption = "TrackSwipe raw input", Parent = HWND_MESSAGE });
        var registration = new RAWINPUTDEVICE
        {
            UsagePage = DigitizerPage,
            Usage = UsageTouchPad,
            Flags = RIDEV_INPUTSINK,
            Target = Handle,
        };
        if (!RegisterRawInputDevices(new[] { registration }, 1, (uint)Marshal.SizeOf<RAWINPUTDEVICE>()))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not register for touchpad input.");
    }

    public event Action<TouchFrame>? FrameReceived;

    public event Action<TouchpadDevice>? DeviceFound;

    public void Dispose()
    {
        DestroyHandle();
        foreach (var device in _devices.Values) device?.Dispose();
        _devices.Clear();
        if (_buffer != IntPtr.Zero) Marshal.FreeHGlobal(_buffer);
        _buffer = IntPtr.Zero;
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg != WM_INPUT)
        {
            base.WndProc(ref m);
            return;
        }

        try
        {
            ReadInput(m.LParam);
        }
        finally
        {
            base.WndProc(ref m); // DefWindowProc frees the raw input buffer
        }
    }

    private void ReadInput(IntPtr rawInput)
    {
        uint size = 0;
        if (GetRawInputData(rawInput, RID_INPUT, IntPtr.Zero, ref size, (uint)_headerSize) != 0 || size == 0) return;
        EnsureBuffer((int)size);
        if (GetRawInputData(rawInput, RID_INPUT, _buffer, ref size, (uint)_headerSize) == uint.MaxValue) return;

        var header = Marshal.PtrToStructure<RAWINPUTHEADER>(_buffer);
        if (header.Type != RIM_TYPEHID || GetDevice(header.Device) is not { } device) return;

        // RAWHID: dwSizeHid, dwCount, then dwCount reports of dwSizeHid bytes each.
        var reportSize = Marshal.ReadInt32(_buffer, _headerSize);
        var reportCount = Marshal.ReadInt32(_buffer, _headerSize + 4);
        var reports = _buffer + _headerSize + 8;
        var time = Clock.Now;
        for (var i = 0; i < reportCount; i++)
        {
            if (device.Parse(reports + i * reportSize, (uint)reportSize, time) is { } frame)
                FrameReceived?.Invoke(frame);
        }
    }

    private TouchpadDevice? GetDevice(IntPtr handle)
    {
        if (_devices.TryGetValue(handle, out var device)) return device;
        device = TouchpadDevice.TryCreate(handle);
        _devices[handle] = device;
        if (device is not null) DeviceFound?.Invoke(device);
        return device;
    }

    private void EnsureBuffer(int size)
    {
        if (size <= _bufferSize) return;
        if (_buffer != IntPtr.Zero) Marshal.FreeHGlobal(_buffer);
        _buffer = Marshal.AllocHGlobal(size);
        _bufferSize = size;
    }
}
