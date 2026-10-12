using System.Drawing.Drawing2D;
using TrackSwipe.Core;

namespace TrackSwipe;

/// <summary>Live view of touchpad contacts and engine state, for checking input and tuning.</summary>
internal sealed class InputMonitorForm : Form
{
    private static readonly Color Background = Color.FromArgb(24, 24, 28);
    private static readonly Color Pad = Color.FromArgb(40, 40, 46);
    private static readonly Color Finger = Color.FromArgb(76, 160, 255);
    private static readonly Color Palm = Color.FromArgb(255, 150, 60);
    private static readonly Color Lifted = Color.FromArgb(120, 120, 128);

    private readonly string _device;
    private TouchFrame? _frame;
    private string _status = "Waiting for touchpad input...";
    private int _framesThisSecond, _reportRate;
    private double _rateWindowStart;

    public InputMonitorForm(string device)
    {
        _device = device;
        Text = "TrackSwipe - input monitor";
        AutoScaleMode = AutoScaleMode.Dpi;
        AutoScaleDimensions = new SizeF(96, 96);
        ClientSize = new Size(560, 440);
        MinimumSize = new Size(360, 300);
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = Background;
        ForeColor = Color.Gainsboro;
        Font = new Font("Segoe UI", 9.5f);
        DoubleBuffered = true;
        ResizeRedraw = true;
    }

    public void Push(TouchFrame frame, GestureEngine engine, bool enabled)
    {
        _frame = frame;
        _framesThisSecond++;
        if (frame.Time - _rateWindowStart >= 1)
        {
            _reportRate = _framesThisSecond;
            _framesThisSecond = 0;
            _rateWindowStart = frame.Time;
        }

        _status =
            $"{_device}\n" +
            $"Frames {_reportRate}/s    Active contacts {engine.ActiveContacts}    Gestures {(enabled ? "on" : "paused")}\n" +
            $"Phase {engine.Phase}    Axis {engine.Axis}\n" +
            $"Progress {engine.Progress:+0.00;-0.00}    Display {engine.DisplayProgress:+0.00;-0.00}    Velocity {engine.Velocity:+0.0;-0.0}/s";
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var margin = LogicalToDeviceUnits(14);
        var textHeight = Font.Height * 4 + margin / 2;
        TextRenderer.DrawText(g, _status, Font, new Rectangle(margin, margin, ClientSize.Width - 2 * margin, textHeight), ForeColor, TextFormatFlags.Left);

        var frame = _frame;
        if (frame is null) return;

        var area = new Rectangle(margin, margin * 2 + textHeight, ClientSize.Width - 2 * margin, ClientSize.Height - textHeight - margin * 3);
        if (area.Width <= 0 || area.Height <= 0) return;
        var scale = (float)Math.Min(area.Width / frame.PadWidthMm, area.Height / frame.PadHeightMm);
        var pad = new RectangleF(area.X, area.Y, (float)frame.PadWidthMm * scale, (float)frame.PadHeightMm * scale);
        using (var padBrush = new SolidBrush(Pad)) g.FillRectangle(padBrush, pad);

        var radius = 5 * scale; // roughly a fingertip
        foreach (var contact in frame.Contacts)
        {
            var center = new PointF(pad.X + (float)contact.X * scale, pad.Y + (float)contact.Y * scale);
            var circle = new RectangleF(center.X - radius, center.Y - radius, radius * 2, radius * 2);
            var color = !contact.Tip ? Lifted : contact.Confidence ? Finger : Palm;
            if (contact.Tip)
            {
                using var brush = new SolidBrush(color);
                g.FillEllipse(brush, circle);
            }
            else
            {
                using var pen = new Pen(color, 2);
                g.DrawEllipse(pen, circle);
            }
            TextRenderer.DrawText(g, contact.Id.ToString(), Font, Rectangle.Round(circle), Color.White,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        }
    }
}
