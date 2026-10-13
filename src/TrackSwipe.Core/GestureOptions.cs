namespace TrackSwipe.Core;

public sealed class GestureOptions
{
    /// <summary>Number of fingers that drive the gesture.</summary>
    public int Fingers { get; set; } = 4;

    /// <summary>Finger travel (mm) before the gesture locks to an axis.</summary>
    public double DeadZoneMm { get; set; } = 1;

    /// <summary>Horizontal finger travel (mm) that equals progress 1.0, i.e. one whole desktop.</summary>
    public double FullSwipeMm { get; set; } = 55;

    /// <summary>Vertical finger travel (mm) that equals progress 1.0. Shorter, because the pad is.</summary>
    public double FullSwipeVerticalMm { get; set; } = 40;

    /// <summary>The dominant axis must exceed the other by this ratio to lock.</summary>
    public double AxisLockRatio { get; set; } = 1.3;

    /// <summary>Progress past which a slow release commits.</summary>
    public double CommitProgress { get; set; } = 0.5;

    /// <summary>Release velocity (progress per second) that counts as a flick.</summary>
    public double FlickVelocity { get; set; } = 1.5;

    /// <summary>Minimum progress for a flick to commit, so a twitch doesn't switch desktops.</summary>
    public double MinFlickProgress { get; set; } = 0.05;

    /// <summary>Progress past which a slow vertical release commits.</summary>
    public double VerticalCommitProgress { get; set; } = 0.35;

    /// <summary>Furthest the panel stretches when there is no desktop in that direction.</summary>
    public double RubberBandLimit { get; set; } = 0.12;

    /// <summary>Seconds without touchpad reports before a gesture is treated as released.</summary>
    public double ReleaseTimeout { get; set; } = 0.5;

    /// <summary>How close to its destination the panel must get before the action fires.</summary>
    public double CommitTolerance { get; set; } = 0.02;

    public SpringPreset Spring { get; set; } = SpringPreset.Snappy;
}
