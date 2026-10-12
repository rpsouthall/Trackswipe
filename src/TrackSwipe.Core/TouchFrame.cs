namespace TrackSwipe.Core;

/// <summary>One finger contact, in millimetres from the touchpad's top-left corner.</summary>
public readonly record struct Contact(int Id, double X, double Y, bool Tip, bool Confidence)
{
    /// <summary>Touching the pad and not flagged by the device as a palm.</summary>
    public bool IsActive => Tip && Confidence;
}

/// <summary>Every contact reported for one touchpad scan. <see cref="Time"/> is in seconds.</summary>
public sealed record TouchFrame(double Time, IReadOnlyList<Contact> Contacts, double PadWidthMm, double PadHeightMm);
