namespace TrackSwipe.Core;

/// <summary>
/// Recognises a quick tap with a set number of fingers: they land together, barely move, and start lifting soon after.
/// It's judged as the first finger lifts, because the touchpad reader stops delivering reports once fewer fingers than
/// a gesture needs remain. Not thread-safe: call <see cref="Process"/> with every delivered frame, from one thread.
/// </summary>
public sealed class TapDetector
{
    private readonly Dictionary<int, (double X, double Y)> _landed = new();
    private bool _touching, _spoiled;
    private int _most;
    private int _leftover; // fingers still down after a touch was judged
    private double _since;
    private double _lastFrameTime = double.NegativeInfinity;

    /// <summary>Fingers in the tap.</summary>
    public int Fingers { get; set; } = 3;

    /// <summary>Longest a tap may last, in seconds, from the first finger landing to the first lifting.</summary>
    public double MaxDuration { get; set; } = 0.3;

    /// <summary>How long after the first finger the others may land, in seconds.</summary>
    public double LandingWindow { get; set; } = 0.12;

    /// <summary>Furthest (mm) any finger may move while down.</summary>
    public double MaxTravelMm { get; set; } = 2.5;

    /// <summary>A silence (seconds) after which the last touch is taken as over, as for gestures.</summary>
    public double ReleaseTimeout { get; set; } = 0.5;

    public event Action? Tapped;

    /// <summary>The current touch isn't a tap after all (it became a swipe, say).</summary>
    public void Cancel() => _spoiled = true;

    public void Process(TouchFrame frame)
    {
        if (frame.Time - _lastFrameTime > ReleaseTimeout)
        {
            // A lift went unreported: whatever was down has gone.
            _touching = false;
            _leftover = 0;
        }
        _lastFrameTime = frame.Time;

        var count = 0;
        for (var i = 0; i < frame.Contacts.Count; i++) if (frame.Contacts[i].IsActive) count++;
        if (count == 0)
        {
            if (_touching) Judge(frame.Time, 0); // every finger lifted at once
            _touching = false;
            _leftover = 0;
            return;
        }

        if (!_touching)
        {
            // Fingers still lifting from the last touch aren't a new one; a finger landing is.
            if (count <= _leftover) { _leftover = count; return; }
            _leftover = 0;
            _touching = true;
            _spoiled = false;
            _most = 0;
            // From the first finger's landing, if the reader saw it before this frame (two fingers resting or
            // scrolling, then a third joining, isn't a tap).
            _since = frame.TouchStarted > 0 ? Math.Min(frame.TouchStarted, frame.Time) : frame.Time;
            _landed.Clear();
        }

        for (var i = 0; i < frame.Contacts.Count; i++)
        {
            var contact = frame.Contacts[i];
            if (!contact.IsActive) continue;
            if (!_landed.TryGetValue(contact.Id, out var landed))
            {
                // A finger arriving well after the others is a different gesture (or a resting hand) joining in.
                if (frame.Time - _since > LandingWindow) _spoiled = true;
                _landed[contact.Id] = (contact.X, contact.Y);
            }
            else if (Math.Abs(contact.X - landed.X) > MaxTravelMm || Math.Abs(contact.Y - landed.Y) > MaxTravelMm) _spoiled = true;
        }

        if (count > Fingers) _spoiled = true;
        _most = Math.Max(_most, count);

        if (_most == Fingers && count < Fingers) Judge(frame.Time, count);
    }

    /// <summary>The first finger lifting ends a touch: a tap if they were all down, still, and quick.</summary>
    private void Judge(double time, int remaining)
    {
        var tapped = _most == Fingers && !_spoiled && time - _since <= MaxDuration;
        _touching = false;
        _leftover = remaining; // the fingers still lifting belong to this touch
        if (tapped) Tapped?.Invoke();
    }
}
