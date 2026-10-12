using System.Runtime.InteropServices;
using TrackSwipe.Core;
using TrackSwipe.Interop;
using static TrackSwipe.Interop.NativeMethods;

namespace TrackSwipe.Touchpad;

/// <summary>
/// One precision touchpad, described by its HID report descriptor. Turns raw input reports
/// into <see cref="TouchFrame"/>s, joining "hybrid mode" scans that span several reports.
/// </summary>
internal sealed class TouchpadDevice : IDisposable
{
    private const ushort GenericDesktopPage = 0x01, DigitizerPage = 0x0D;
    private const ushort UsageTouchPad = 0x05, UsageX = 0x30, UsageY = 0x31;
    private const ushort UsageTipSwitch = 0x42, UsageConfidence = 0x47, UsageContactId = 0x51, UsageContactCount = 0x54;

    private readonly IntPtr _preparsed;
    private readonly Slot[] _slots;
    private readonly ushort? _countCollection;
    private readonly ushort[] _usages = new ushort[32];
    private readonly List<Contact> _pending = new(5);
    private int _expected;

    private TouchpadDevice(IntPtr preparsed, Slot[] slots, ushort? countCollection, double widthMm, double heightMm, bool sizeKnown)
    {
        _preparsed = preparsed;
        _slots = slots;
        _countCollection = countCollection;
        WidthMm = widthMm;
        HeightMm = heightMm;
        SizeKnown = sizeKnown;
    }

    public int SlotCount => _slots.Length;
    public double WidthMm { get; }
    public double HeightMm { get; }

    /// <summary>False when the descriptor gave no usable physical size and we guessed.</summary>
    public bool SizeKnown { get; }

    public static TouchpadDevice? TryCreate(IntPtr device)
    {
        uint size = 0;
        GetRawInputDeviceInfo(device, RIDI_PREPARSEDDATA, IntPtr.Zero, ref size);
        if (size == 0) return null;

        var preparsed = Marshal.AllocHGlobal((int)size);
        TouchpadDevice? created = null;
        try
        {
            if (GetRawInputDeviceInfo(device, RIDI_PREPARSEDDATA, preparsed, ref size) == uint.MaxValue) return null;
            if (HidP_GetCaps(preparsed, out var caps) != HIDP_STATUS_SUCCESS) return null;
            if (caps.UsagePage != DigitizerPage || caps.Usage != UsageTouchPad) return null;

            var valueCaps = new HIDP_VALUE_CAPS[caps.NumberInputValueCaps];
            var count = caps.NumberInputValueCaps;
            if (HidP_GetValueCaps(HidP_Input, valueCaps, ref count, preparsed) != HIDP_STATUS_SUCCESS) return null;

            var xs = new Dictionary<ushort, HIDP_VALUE_CAPS>();
            var ys = new Dictionary<ushort, HIDP_VALUE_CAPS>();
            ushort? countCollection = null;
            foreach (var vc in valueCaps.AsSpan(0, count))
            {
                if (vc.IsRange != 0) continue;
                switch (vc.UsagePage, vc.Usage)
                {
                    case (GenericDesktopPage, UsageX): xs[vc.LinkCollection] = vc; break;
                    case (GenericDesktopPage, UsageY): ys[vc.LinkCollection] = vc; break;
                    case (DigitizerPage, UsageContactCount): countCollection = vc.LinkCollection; break;
                }
            }

            // Each finger lives in its own link collection holding an X and a Y.
            var slots = xs.Keys.Where(ys.ContainsKey).Order()
                .Select(link => new Slot(link, Axis.From(xs[link]), Axis.From(ys[link])))
                .ToArray();
            if (slots.Length == 0) return null;

            var first = slots[0].Collection;
            var width = PhysicalMm(xs[first]);
            var height = PhysicalMm(ys[first]);
            var sizeKnown = width is not null && height is not null;
            if (!sizeKnown)
            {
                // Guess a typical pad width and assume square logical units.
                width = 100;
                height = 100 * slots[0].Y.Span / slots[0].X.Span;
            }

            created = new TouchpadDevice(preparsed, slots, countCollection, width!.Value, height!.Value, sizeKnown);
            return created;
        }
        finally
        {
            if (created is null) Marshal.FreeHGlobal(preparsed);
        }
    }

    /// <summary>Parses one input report. Returns a frame once every contact of the current scan has arrived.</summary>
    public TouchFrame? Parse(IntPtr report, uint length, double time)
    {
        // The first report of a scan carries the contact count; continuation reports carry zero.
        if (_countCollection is { } countCollection
            && HidP_GetUsageValue(HidP_Input, DigitizerPage, countCollection, UsageContactCount, out var reported, _preparsed, report, length) == HIDP_STATUS_SUCCESS
            && reported > 0)
        {
            _pending.Clear();
            _expected = (int)reported;
        }
        if (_expected == 0) return null;

        var toRead = Math.Min(_slots.Length, _expected - _pending.Count);
        for (var i = 0; i < toRead; i++)
        {
            if (!TryReadContact(_slots[i], report, length, out var contact))
            {
                _pending.Clear();
                _expected = 0;
                return null;
            }
            _pending.Add(contact);
        }
        if (_pending.Count < _expected) return null;

        var frame = new TouchFrame(time, _pending.ToArray(), WidthMm, HeightMm);
        _pending.Clear();
        _expected = 0;
        return frame;
    }

    public void Dispose() => Marshal.FreeHGlobal(_preparsed);

    public override string ToString() =>
        $"{SlotCount} contact slots, {WidthMm:0.#} x {HeightMm:0.#} mm{(SizeKnown ? "" : " (size guessed)")}, " +
        $"contact count {(_countCollection is null ? "missing" : "present")}";

    private bool TryReadContact(Slot slot, IntPtr report, uint length, out Contact contact)
    {
        contact = default;
        if (HidP_GetUsageValue(HidP_Input, GenericDesktopPage, slot.Collection, UsageX, out var x, _preparsed, report, length) != HIDP_STATUS_SUCCESS
            || HidP_GetUsageValue(HidP_Input, GenericDesktopPage, slot.Collection, UsageY, out var y, _preparsed, report, length) != HIDP_STATUS_SUCCESS)
            return false;
        if (HidP_GetUsageValue(HidP_Input, DigitizerPage, slot.Collection, UsageContactId, out var id, _preparsed, report, length) != HIDP_STATUS_SUCCESS)
            id = slot.Collection;

        bool tip = false, confidence = false;
        var usageCount = (uint)_usages.Length;
        if (HidP_GetUsages(HidP_Input, DigitizerPage, slot.Collection, _usages, ref usageCount, _preparsed, report, length) == HIDP_STATUS_SUCCESS)
        {
            foreach (var usage in _usages.AsSpan(0, (int)usageCount))
            {
                if (usage == UsageTipSwitch) tip = true;
                else if (usage == UsageConfidence) confidence = true;
            }
        }

        contact = new Contact((int)id, slot.X.Normalize(x) * WidthMm, slot.Y.Normalize(y) * HeightMm, tip, confidence);
        return true;
    }

    private static double? PhysicalMm(HIDP_VALUE_CAPS caps)
    {
        double range = caps.PhysicalMax - caps.PhysicalMin;
        var exponent = (int)(caps.UnitsExp & 0xF); // 4-bit two's complement
        if (exponent > 7) exponent -= 16;
        double? mmPerUnit = caps.Units switch { 0x11 => 10, 0x13 => 25.4, _ => null }; // centimetres, inches
        if (range <= 0 || mmPerUnit is null) return null;

        var mm = range * Math.Pow(10, exponent) * mmPerUnit.Value;
        return mm is > 20 and < 400 ? mm : null;
    }

    private readonly record struct Axis(int Min, int Max)
    {
        public double Span => Math.Max(1, Max - Min);

        public static Axis From(HIDP_VALUE_CAPS caps) => new(caps.LogicalMin, caps.LogicalMax);

        public double Normalize(uint value) => Math.Clamp((value - (double)Min) / Span, 0, 1);
    }

    private readonly record struct Slot(ushort Collection, Axis X, Axis Y);
}
