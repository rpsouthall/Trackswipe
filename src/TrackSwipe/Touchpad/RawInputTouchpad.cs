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
        EnsureBuffer(1024); // a touchpad report with its headers is a few hundred bytes
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
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // This runs on the touchpad thread; one unreadable report is not worth a crash.
            Log.Write($"Touchpad report skipped: {ex.Message}");
        }
        finally
        {
            base.WndProc(ref m); // DefWindowProc frees the raw input buffer
        }
    }

    /// <summary>Whether the last report came from a touchpad that stamps its scans, so reports can be read late without losing their timing.</summary>
    public bool Timestamped { get; private set; }

    private void ReadInput(IntPtr rawInput)
    {
        // One call into the system per report: the buffer is normally big enough already, so the size is only
        // asked for (a second call) when it isn't.
        uint size = (uint)_bufferSize;
        var copied = GetRawInputData(rawInput, RID_INPUT, _buffer, ref size, (uint)_headerSize);
        if (copied == uint.MaxValue || copied == 0)
        {
            size = 0;
            if (GetRawInputData(rawInput, RID_INPUT, IntPtr.Zero, ref size, (uint)_headerSize) != 0 || size == 0) return;
            EnsureBuffer((int)size);
            copied = GetRawInputData(rawInput, RID_INPUT, _buffer, ref size, (uint)_headerSize);
            if (copied == uint.MaxValue || copied == 0) return;
        }
        Process(_buffer, copied, Clock.Now);
    }

    /// <summary>
    /// Reads every report waiting in this thread's queue, oldest first, a buffer-full per call into the system rather
    /// than one wake-up and call per report. Reports read late keep their timing: it comes from the touchpad's own scan
    /// clock, anchored when the touch began. Returns how many reports there were.
    /// </summary>
    public unsafe int ReadBuffered()
    {
        EnsureBuffer(BatchBufferSize);
        var total = 0;
        var time = Clock.Now;
        while (true)
        {
            uint size = (uint)_bufferSize;
            var count = GetRawInputBuffer(_buffer, ref size, (uint)_headerSize);
            if (count == 0 || count == uint.MaxValue) return total;
            var block = (byte*)_buffer;
            var end = block + _bufferSize;
            for (var i = 0; i < count; i++)
            {
                var length = ((RAWINPUTHEADER*)block)->Size;
                if (length < _headerSize || block + length > end) return total;
                Process((IntPtr)block, length, time);
                block = (byte*)(((nint)block + (nint)length + 7) & ~(nint)7); // NEXTRAWINPUTBLOCK: each one 8-byte aligned
            }
            total += (int)count;
        }
    }

    /// <summary>Room for a few dozen touchpad reports per call when reading a batch.</summary>
    private const int BatchBufferSize = 16 * 1024;

    private unsafe void Process(IntPtr input, uint size, double time)
    {
        var header = (RAWINPUTHEADER*)input;
        if (header->Type != RIM_TYPEHID || GetDevice(header->Device) is not { } device) return;
        Timestamped = device.HasScanTime;

        // RAWHID: dwSizeHid, dwCount, then dwCount reports of dwSizeHid bytes each.
        var reportSize = *(int*)(input + _headerSize);
        var reportCount = *(int*)(input + _headerSize + 4);
        var reports = input + _headerSize + 8;
        if (reportSize <= 0 || reportCount <= 0 || (long)reportSize * reportCount > size - _headerSize - 8) return;
        device.BeginBatch(reports + (reportCount - 1) * reportSize, (uint)reportSize, time);
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
        size = (size + 7) & ~7;
        _buffer = Marshal.AllocHGlobal(size);
        _bufferSize = size;
    }
}
