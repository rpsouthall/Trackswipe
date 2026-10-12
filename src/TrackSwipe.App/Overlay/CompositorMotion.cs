using TrackSwipe.Interop;
using Vortice.DirectComposition;

namespace TrackSwipe.Overlay;

internal readonly record struct SpriteFrame(double Time, RectangleF Bounds, float Opacity, RectangleF? Viewport);

/// <summary>Submits scalar curves once; Windows evaluates them even while the app's UI thread is busy.</summary>
internal static class CompositorMotion
{
    public static IDCompositionAnimation Curve(IReadOnlyList<SpriteFrame> frames, Func<SpriteFrame, float> value, long begin)
    {
        var animation = Composition.Device.CreateAnimation();
        try
        {
            animation.SetAbsoluteBeginTime(begin).CheckError();
            var constant = value(frames[0]);
            if (frames.All(frame => value(frame) == constant))
            {
                animation.AddCubic(0, constant, 0, 0, 0).CheckError();
                animation.End(frames[^1].Time, constant).CheckError();
                return animation;
            }
            for (var i = 0; i < frames.Count - 1; i++)
            {
                var a = frames[i];
                var b = frames[i + 1];
                var first = value(a);
                animation.AddCubic(a.Time, first, (value(b) - first) / (float)(b.Time - a.Time), 0, 0).CheckError();
            }
            animation.End(frames[^1].Time, value(frames[^1])).CheckError();
            return animation;
        }
        catch { animation.Dispose(); throw; }
    }

}
