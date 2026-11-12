using System.Runtime.InteropServices;
using Anchor.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace Anchor;

/// <summary>
/// The dock as the dash of TrackSwipe's GNOME-style overview (<see cref="TrackSwipeBridge.OverviewOnly"/>): a rounded
/// panel a little lighter than the overview's dark grey, as GNOME's dash is, in place of the glass.
/// <para>
/// The panel is drawn by the dock itself and is opaque, so nothing has to be blurred or seen through behind a dock
/// that slides in over a moving overview. Its corners are rounder than a window's: the panel is a rounded border, the
/// window behind it is filled with the overview's own grey (so the panel's smooth edge blends into the overview), and
/// the window is cut to the same rounded shape, so that fill never shows as square corners over anything else.
/// </para>
/// Partial of <see cref="DockWindow"/>.
/// </summary>
public sealed partial class DockWindow
{
    /// <summary>The plain dark grey TrackSwipe's GNOME-style overview puts behind its desktops (its Swatches.OverviewGrey).</summary>
    private static readonly Windows.UI.Color OverviewGrey = Windows.UI.Color.FromArgb(255, 30, 30, 33);

    /// <summary>The dash's panel: a step lighter than the overview behind it.</summary>
    private static readonly Windows.UI.Color DashGrey = Windows.UI.Color.FromArgb(255, 54, 54, 58);

    /// <summary>The panel's corner radius, in DIPs.</summary>
    private const double DashRadius = 20;

    /// <summary>Rows at the top of the window that the dock's content doesn't cover, in pixels.</summary>
    private const int TopLine = 1;

    private static readonly Brush NoFill = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
    private Brush? _dashBehind;
    private Border? _dashPanel;
    private (int Width, int Height, int Radius) _dashShape;

    /// <summary>Puts the dash's panel behind the icons, or takes it away again.</summary>
    private void ApplyDashPanel(bool dash)
    {
        RootGrid.Background = dash ? _dashBehind ??= new SolidColorBrush(OverviewGrey) : NoFill;
        if (dash)
        {
            if (_dashPanel is not null)
                return;
            _dashPanel = new Border
            {
                Background = new SolidColorBrush(DashGrey),
                CornerRadius = new CornerRadius(DashRadius),
                IsHitTestVisible = false, // the icons, and the dock's own drag and drop, are in front of it
            };
            RootGrid.Children.Insert(0, _dashPanel); // behind everything else in the dock
        }
        else if (_dashPanel is not null)
        {
            RootGrid.Children.Remove(_dashPanel);
            _dashPanel = null;
        }
    }

    /// <summary>Cuts the window to the panel's rounded shape. Called after every relayout: the dock grows and shrinks.</summary>
    private void ApplyDashShape()
    {
        if (!GetWindowRect(_hwnd, out var rect))
            return;
        int width = rect.Right - rect.Left, height = rect.Bottom - rect.Top;
        uint dpi = GetDpiForWindow(_hwnd);
        int radius = (int)Math.Round(DashRadius * (dpi == 0 ? 96 : dpi) / 96.0);
        if (width <= 0 || height <= 0 || _dashShape == (width, height, radius))
            return;
        // From the second row down: the window's very top row isn't the dock's to draw (WinUI leaves a one-pixel
        // line there, which showed as a white stroke along the panel's top), and the panel starts under it.
        nint region = CreateRoundRectRgn(0, TopLine, width + 1, height + 1, 2 * radius, 2 * radius);
        if (region == 0)
            return;
        if (SetWindowRgn(_hwnd, region, true) != 0)
            _dashShape = (width, height, radius); // the window owns the region now
        else
            DeleteObject(region);
    }

    /// <summary>Back to a whole window, with Windows' own corners.</summary>
    private void ClearDashShape()
    {
        if (_dashShape == default)
            return;
        SetWindowRgn(_hwnd, 0, true);
        _dashShape = default;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WindowRect
    {
        public int Left, Top, Right, Bottom;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(nint hwnd, out WindowRect rect);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(nint hwnd);

    [DllImport("user32.dll")]
    private static extern int SetWindowRgn(nint hwnd, nint region, [MarshalAs(UnmanagedType.Bool)] bool redraw);

    [DllImport("gdi32.dll")]
    private static extern nint CreateRoundRectRgn(int left, int top, int right, int bottom, int ellipseWidth, int ellipseHeight);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteObject(nint handle);
}
