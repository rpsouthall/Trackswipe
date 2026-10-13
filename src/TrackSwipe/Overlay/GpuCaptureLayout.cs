using System.Drawing;
using System.Numerics;

namespace TrackSwipe.Overlay;

internal static class GpuCaptureLayout
{
    public static Matrix3x2 Transform(Size source, int rotation, PointF offset)
    {
        var orient = rotation switch
        {
            90 => new Matrix3x2(0, 1, -1, 0, source.Height, 0),
            180 => new Matrix3x2(-1, 0, 0, -1, source.Width, source.Height),
            270 => new Matrix3x2(0, -1, 1, 0, 0, source.Width),
            _ => Matrix3x2.Identity,
        };
        return orient * Matrix3x2.CreateTranslation(offset.X, offset.Y);
    }
}
