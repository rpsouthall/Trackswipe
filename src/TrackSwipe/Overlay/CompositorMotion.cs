using TrackSwipe.Interop;
using Vortice.DirectComposition;

namespace TrackSwipe.Overlay;

internal readonly record struct SpriteFrame(double Time, RectangleF Bounds, float Opacity, RectangleF? Viewport);

/// <summary>Submits scalar curves once; Windows evaluates them even while the app's UI thread is busy.</summary>
internal static class CompositorMotion
{
    public static IDCompositionAnimation Curve(IReadOnlyList<SpriteFrame> frames, Func<SpriteFrame, float> value, long begin)
    {
        var times = new double[frames.Count];
        var values = new double[frames.Count];
        for (var i = 0; i < frames.Count; i++) { times[i] = frames[i].Time; values[i] = value(frames[i]); }
        var animation = Composition.Device.CreateAnimation();
        try
        {
            animation.SetAbsoluteBeginTime(begin).CheckError();
            foreach (var segment in CurveFit.Segments(times, values))
                animation.AddCubic(segment.Start, (float)segment.C0, (float)segment.C1, (float)segment.C2, (float)segment.C3).CheckError();
            animation.End(times[^1], (float)values[^1]).CheckError();
            return animation;
        }
        catch { animation.Dispose(); throw; }
    }
}
