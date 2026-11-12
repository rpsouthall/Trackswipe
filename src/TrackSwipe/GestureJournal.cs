using System.Globalization;
using System.Text;
using TrackSwipe.Core;

namespace TrackSwipe;

/// <summary>
/// With verbose logging on, one line in the log per gesture, enough to see afterwards why a swipe didn't read as one
/// movement: how many fingers the touchpad reported and for how long, what each release was judged on and where it
/// was sent, whether fingers caught it again, and the turning points of where the panel was drawn. Numbers only: no
/// finger positions. Not created at all without verbose logging. On the composition thread.
/// </summary>
internal sealed class GestureJournal
{
    /// <summary>How far (progress) the panel must come back to count as having turned.</summary>
    private const double Turn = 0.015;

    private readonly GestureEngine _engine;
    private readonly List<(int Count, double Milliseconds)> _fingers = new();
    private readonly List<string> _events = new();
    private readonly List<double> _path = new();
    private bool _active;
    private GestureAxis _axis;
    private double _started, _runStarted, _lastFrame, _releasedAt = double.NaN;
    private int _count = -1, _direction, _dropoutsBefore;
    private double _extreme;

    public GestureJournal(GestureEngine engine)
    {
        _engine = engine;
        engine.TrackingStarted += Begin;
        engine.SettlingStarted += _ => Released();
        engine.SettlingInterrupted += Caught;
        engine.Committed += action => { if (_active) _events.Add($"committed {action}"); };
        engine.Ended += End;
    }

    private void Begin(GestureAxis axis)
    {
        _active = true;
        _axis = axis;
        _started = _runStarted = _lastFrame = Clock.Now;
        _count = -1;
        _fingers.Clear();
        _events.Clear();
        _path.Clear();
        _direction = 0;
        _extreme = 0;
        _releasedAt = double.NaN;
        _dropoutsBefore = _engine.DropoutsAbsorbed;
    }

    /// <summary>A touchpad report reached the engine.</summary>
    public void Frame(TouchFrame frame)
    {
        if (!_active) return;
        var count = 0;
        for (var i = 0; i < frame.Contacts.Count; i++) if (frame.Contacts[i].IsActive) count++;
        if (count != _count)
        {
            if (_count >= 0) _fingers.Add((_count, (frame.Time - _runStarted) * 1000));
            _count = count;
            _runStarted = frame.Time;
        }
        _lastFrame = frame.Time;
    }

    /// <summary>A display frame was drawn at the engine's current position.</summary>
    public void Display()
    {
        if (!_active) return;
        var shown = _engine.DisplayProgress;
        if (_direction == 0)
        {
            if (Math.Abs(shown - _extreme) <= Turn) return;
            _path.Add(_extreme);
            _direction = Math.Sign(shown - _extreme);
            _extreme = shown;
        }
        else if ((shown - _extreme) * _direction > 0) _extreme = shown;
        else if ((_extreme - shown) * _direction > Turn)
        {
            _path.Add(_extreme); // it turned here
            _direction = -_direction;
            _extreme = shown;
        }
    }

    private void Released()
    {
        if (!_active) return;
        var (progress, velocity, target) = _engine.LastRelease;
        _releasedAt = Clock.Now;
        _events.Add(string.Create(CultureInfo.InvariantCulture,
            $"released {(_releasedAt - _started) * 1000:0} ms in at {progress:0.00} ({velocity:0.0}/s) for {target:0}"));
    }

    private void Caught()
    {
        if (!_active) return;
        var after = double.IsNaN(_releasedAt) ? 0 : (Clock.Now - _releasedAt) * 1000;
        _events.Add(string.Create(CultureInfo.InvariantCulture, $"caught again {after:0} ms later at {_engine.DisplayProgress:0.00}"));
        // A swipe moved on to its desktop is drawn from there: the panel's path starts again.
        _path.Add(_extreme);
        _path.Add(double.NaN);
        _direction = 0;
        _extreme = _engine.DisplayProgress;
    }

    private void End()
    {
        if (!_active) return;
        _active = false;
        if (_count >= 0) _fingers.Add((_count, Math.Max(0, (_lastFrame - _runStarted) * 1000)));
        _path.Add(_extreme);
        var line = new StringBuilder(256);
        line.Append(CultureInfo.InvariantCulture, $"Gesture {_axis}, {(Clock.Now - _started) * 1000:0} ms: fingers");
        foreach (var (count, milliseconds) in _fingers) line.Append(CultureInfo.InvariantCulture, $" {count}x{milliseconds:0}ms");
        foreach (var entry in _events) line.Append("; ").Append(entry);
        line.Append("; panel");
        var turns = 0;
        for (var i = 0; i < _path.Count; i++)
        {
            if (double.IsNaN(_path[i])) { line.Append(" |"); continue; }
            line.Append(CultureInfo.InvariantCulture, $" {_path[i]:0.00}");
            if (i > 0 && i < _path.Count - 1 && !double.IsNaN(_path[i - 1]) && !double.IsNaN(_path[i + 1])) turns++;
        }
        if (turns > 0) line.Append(CultureInfo.InvariantCulture, $" ({turns} turn{(turns == 1 ? "" : "s")})");
        var absorbed = _engine.DropoutsAbsorbed - _dropoutsBefore;
        if (absorbed > 0) line.Append(CultureInfo.InvariantCulture, $"; fingers lost and found mid-swipe {absorbed}x (ignored)");
        Log.Write(line.ToString());
    }
}
