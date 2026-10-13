namespace TrackSwipe;

/// <summary>Fixed storage even when fingers keep the animation clock running for a long time.</summary>
internal sealed class AnimationStatistics
{
    private const double BinSeconds = 0.0001;
    private readonly int[] _gapHistogram = new int[2049];
    private readonly (int Frame, double Gap)[] _slowGaps = new (int, double)[32];
    private double _previous = double.NaN, _workTotal;
    public int Frames { get; private set; }
    public double Span { get; private set; }
    public double WorstGap { get; private set; }
    public double WorstWork { get; private set; }
    public int SlowGaps { get; private set; }
    public double AverageWork => Frames == 0 ? 0 : _workTotal / Frames;
    public ReadOnlySpan<(int Frame, double Gap)> SlowSamples => _slowGaps.AsSpan(0, Math.Min(SlowGaps, _slowGaps.Length));

    // Rounded to 0.1 ms; exceptionally long gaps share the final bin. WorstGap stays exact.
    public double MedianGap
    {
        get
        {
            if (Frames < 2) return 0;
            var middle = (Frames - 1) / 2;
            for (var i = 0; i < _gapHistogram.Length; i++)
            {
                middle -= _gapHistogram[i];
                if (middle < 0) return i * BinSeconds;
            }
            return 0;
        }
    }

    public void Add(double started, double finished)
    {
        if (!double.IsNaN(_previous))
        {
            var gap = Math.Max(0, started - _previous);
            Span += gap;
            WorstGap = Math.Max(WorstGap, gap);
            _gapHistogram[(int)Math.Clamp(Math.Round(gap / BinSeconds), 0, _gapHistogram.Length - 1)]++;
            if (gap > 0.010)
            {
                if (SlowGaps < _slowGaps.Length) _slowGaps[SlowGaps] = (Frames, gap);
                SlowGaps++;
            }
        }
        _previous = started;
        var work = Math.Max(0, finished - started);
        _workTotal += work;
        WorstWork = Math.Max(WorstWork, work);
        Frames++;
    }

    public void Reset()
    {
        Array.Clear(_gapHistogram);
        Frames = SlowGaps = 0;
        Span = WorstGap = WorstWork = _workTotal = 0;
        _previous = double.NaN;
    }
}
