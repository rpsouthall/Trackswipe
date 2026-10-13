namespace TrackSwipe.Touchpad;

/// <summary>Which touchpad reports the gesture engine needs to see.</summary>
internal static class TouchFilter
{
    /// <param name="active">Fingers touching in this report.</param>
    /// <param name="fingers">The gesture's finger count: an idle engine reacts to nothing less.</param>
    /// <param name="forwardAll">A gesture is in progress, or every report is wanted (input monitor).</param>
    /// <param name="engaged">Whether that many fingers have been down since the last report delivered with fewer.</param>
    public static bool Deliver(int active, int fingers, bool forwardAll, ref bool engaged)
    {
        if (forwardAll || active >= fingers)
        {
            engaged = true;
            return true;
        }
        if (!engaged) return false;
        engaged = false; // the drop below the gesture's count: the engine must see it
        return true;
    }
}
