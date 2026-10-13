using System.Collections;
using System.Runtime.CompilerServices;

namespace TrackSwipe.Core;

/// <summary>One finger contact, in millimetres from the touchpad's top-left corner.</summary>
public readonly record struct Contact(int Id, double X, double Y, bool Tip, bool Confidence)
{
    /// <summary>Touching the pad and not flagged by the device as a palm.</summary>
    public bool IsActive => Tip && Confidence;
}

/// <summary>Every contact reported for one touchpad scan. <see cref="Time"/> is in seconds.</summary>
public readonly record struct TouchFrame(double Time, ContactSnapshot Contacts, double PadWidthMm, double PadHeightMm)
{
    public TouchFrame(double time, IReadOnlyList<Contact> contacts, double padWidthMm, double padHeightMm)
        : this(time, new ContactSnapshot(contacts), padWidthMm, padHeightMm) { }

    public static TouchFrame FromContacts(double time, ReadOnlySpan<Contact> contacts, double padWidthMm, double padHeightMm) =>
        new(time, new ContactSnapshot(contacts), padWidthMm, padHeightMm);
}

/// <summary>
/// An owned scan snapshot. Normal precision-touchpad scans fit inside the value itself, so the
/// reader can reuse its HID buffer immediately without allocating or sharing mutable contacts.
/// Unusual devices with more than five contacts retain every contact in an owned overflow array.
/// </summary>
public readonly struct ContactSnapshot : IReadOnlyList<Contact>, IEquatable<ContactSnapshot>
{
    [InlineArray(5)]
    private struct InlineContacts { private Contact _first; }

    private readonly InlineContacts _inline;
    private readonly Contact[]? _overflow;
    public int Count { get; }

    public ContactSnapshot(ReadOnlySpan<Contact> contacts)
    {
        Count = contacts.Length;
        _inline = default;
        _overflow = Count > 5 ? contacts.ToArray() : null;
        if (_overflow is null)
            for (var i = 0; i < Count; i++) _inline[i] = contacts[i];
    }

    public ContactSnapshot(IReadOnlyList<Contact> contacts)
    {
        ArgumentNullException.ThrowIfNull(contacts);
        Count = contacts.Count;
        _inline = default;
        _overflow = Count > 5 ? new Contact[Count] : null;
        for (var i = 0; i < Count; i++)
            if (_overflow is null) _inline[i] = contacts[i];
            else _overflow[i] = contacts[i];
    }

    public Contact this[int index] => (uint)index >= (uint)Count
        ? throw new ArgumentOutOfRangeException(nameof(index))
        : _overflow is null ? _inline[index] : _overflow[index];

    public bool Equals(ContactSnapshot other)
    {
        if (Count != other.Count) return false;
        for (var i = 0; i < Count; i++) if (this[i] != other[i]) return false;
        return true;
    }

    public override bool Equals(object? other) => other is ContactSnapshot snapshot && Equals(snapshot);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(Count);
        for (var i = 0; i < Count; i++) hash.Add(this[i]);
        return hash.ToHashCode();
    }

    public Enumerator GetEnumerator() => new(this);
    IEnumerator<Contact> IEnumerable<Contact>.GetEnumerator() => GetEnumerator();
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    public struct Enumerator(ContactSnapshot contacts) : IEnumerator<Contact>
    {
        private int _index = -1;
        public Contact Current => contacts[_index];
        object IEnumerator.Current => Current;
        public bool MoveNext() => ++_index < contacts.Count;
        public void Reset() => _index = -1;
        public void Dispose() { }
    }
}
