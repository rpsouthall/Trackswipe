using Anchor.Interop;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Windows.Graphics;

namespace Anchor.Services;

/// <summary>Native window chrome tweaks that AppWindow/WinUI don't expose directly.</summary>
public static class WindowChrome
{
    /// <summary>Sizes a window's client area given a size in device-independent pixels.</summary>
    public static void SetClientSizeDip(AppWindow appWindow, nint hwnd, double dipW, double dipH)
    {
        double scale = NativeMethods.GetDpiForWindow(hwnd) / 96.0;
        appWindow.Resize(new SizeInt32((int)Math.Ceiling(dipW * scale), (int)Math.Ceiling(dipH * scale)));
    }

    /// <summary>Centers a window on whichever monitor currently hosts the cursor.</summary>
    public static void CenterOnCursor(AppWindow appWindow, WindowId windowId)
    {
        RectInt32 work;
        if (NativeMethods.GetCursorPos(out var p))
            work = DisplayArea.GetFromPoint(new PointInt32(p.X, p.Y), DisplayAreaFallback.Nearest).WorkArea;
        else
            work = DisplayArea.GetFromWindowId(windowId, DisplayAreaFallback.Primary).WorkArea;

        var size = appWindow.Size;
        int x = work.X + (work.Width - size.Width) / 2;
        int y = work.Y + (work.Height - size.Height) / 2;
        appWindow.Move(new PointInt32(x, y));
    }

    /// <summary>
    /// Makes the window a borderless, always-on-top tool window: no title bar or frame,
    /// absent from the taskbar and Alt-Tab, and non-resizable — i.e. dock-like.
    /// </summary>
    public static void MakeBorderlessToolWindow(AppWindow appWindow, nint hwnd)
    {
        if (appWindow.Presenter is OverlappedPresenter p)
        {
            p.SetBorderAndTitleBar(false, false);
            p.IsAlwaysOnTop = true;
            p.IsResizable = false;
            p.IsMaximizable = false;
            p.IsMinimizable = false;
        }

        appWindow.IsShownInSwitchers = false;

        long ex = NativeMethods.GetWindowLongPtr(hwnd, NativeMethods.GWL_EXSTYLE);
        ex |= NativeMethods.WS_EX_TOOLWINDOW;
        NativeMethods.SetWindowLongPtr(hwnd, NativeMethods.GWL_EXSTYLE, (nint)ex);
    }

    /// <summary>
    /// Sets the theme of the system caption buttons (minimize / close) so they render with the
    /// right glyph color from the first frame. Without this the buttons inherit the theme late
    /// (only after activation / a pointer pass over them), so a light-themed window briefly shows
    /// hard-to-see light glyphs.
    /// </summary>
    public static void SetTitleBarTheme(AppWindow appWindow, bool dark)
    {
        appWindow.TitleBar.PreferredTheme = dark ? TitleBarTheme.Dark : TitleBarTheme.Light;
    }

    /// <summary>
    /// Tells DWM which theme to draw the window's non-client frame for.
    /// <para>
    /// This is a separate thing from <see cref="SetTitleBarTheme"/>, which only colors the caption
    /// buttons. Note it does <b>not</b> govern the 1px window rim — that is
    /// <see cref="HideWindowBorder"/>.
    /// </para>
    /// </summary>
    public static void SetFrameTheme(nint hwnd, bool dark)
    {
        int immersiveDark = dark ? 1 : 0;
        NativeMethods.DwmSetWindowAttribute(
            hwnd, NativeMethods.DWMWA_USE_IMMERSIVE_DARK_MODE, ref immersiveDark, sizeof(int));
    }

    /// <summary>Applies the Windows 11 rounded-corner treatment to the window.</summary>
    public static void SetRoundedCorners(nint hwnd, bool small = false)
    {
        int pref = small ? NativeMethods.DWMWCP_ROUNDSMALL : NativeMethods.DWMWCP_ROUND;
        NativeMethods.DwmSetWindowAttribute(
            hwnd, NativeMethods.DWMWA_WINDOW_CORNER_PREFERENCE, ref pref, sizeof(int));
    }

    /// <summary>
    /// Re-applies rounded corners after a frame or layout change. DWM can drop the corner
    /// preference when the non-client frame is stripped or the window is moved/resized — which
    /// shows up most often in installed/MSIX builds where startup timing differs from dev.
    /// </summary>
    public static void EnsureRoundedCorners(nint hwnd, bool small = false)
    {
        SetRoundedCorners(hwnd, small);
        NativeMethods.SetWindowPos(hwnd, 0, 0, 0, 0, 0,
            NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE |
            NativeMethods.SWP_NOZORDER | NativeMethods.SWP_NOACTIVATE |
            NativeMethods.SWP_FRAMECHANGED);
    }

    // The window surface each theme's rim is painted to match, as COLORREFs (0x00BBGGRR). These
    // are WinUI's SolidBackgroundFillColorBase — the color Mica is built on and falls back to —
    // so the rim reads as more window rather than as an edge drawn around it.
    private const int SurfaceLight = 0x00F3F3F3;
    private const int SurfaceDark = 0x00202020;

    /// <summary>
    /// Makes the DWM window rim invisible by painting it the window's own surface color for the
    /// current theme. The immersive-dark-mode flag is tracked to the theme too (see
    /// <see cref="SetFrameTheme"/>) so DWM renders the window edge / corner anti-aliasing for the
    /// right background.
    /// <para>
    /// Used by every Anchor window. On the dock it hides a rim that would otherwise read as a
    /// rectangle drawn around the rounded glass strip. On the Mica dialogs it hides the one piece
    /// of rim those windows still show: they extend their content into the title bar, so the top
    /// edge is all that is left of the frame, and anything that doesn't match the surface reads as
    /// a stray line above the title bar.
    /// </para>
    /// <para>
    /// Painting it is the only option — a rim of zero width is not something DWM offers.
    /// <c>DWMWA_VISIBLE_FRAME_BORDER_THICKNESS</c> is retrieve-only and rejects a set with
    /// <c>E_INVALIDARG</c>, and the <c>DWMWA_COLOR_NONE</c> sentinel does not mean "draw nothing"
    /// here: on a window whose content is extended into the title bar it renders the top edge as
    /// flat white or flat black, following the immersive-dark-mode flag. That is worse than the
    /// default rim, and it is what put a hard white line above a light window and a hard black one
    /// above a dark window — the "residual border" this replaces. Measured, not inferred: with the
    /// sentinel the top row of the frame comes back <c>#FFFFFF</c> in light and <c>#000000</c> in
    /// dark, against Mica surfaces of roughly <c>#F9F0F4</c> and <c>#271C22</c>.
    /// </para>
    /// <para>
    /// An explicit color is also the only one of the two that survives a theme switch: DWM
    /// re-composes the frame for it immediately, with no move, resize or re-activation needed and
    /// nothing left over from the previous theme.
    /// </para>
    /// <para>
    /// A flat COLORREF is all the attribute accepts, while Mica is a blur of the wallpaper and so
    /// varies across the width of the window. The match is therefore very close rather than exact
    /// — single digits per channel, against the tens-to-hundreds that made the old rim obvious.
    /// </para>
    /// </summary>
    public static void HideWindowBorder(nint hwnd, bool dark)
    {
        SetFrameTheme(hwnd, dark);

        int surface = dark ? SurfaceDark : SurfaceLight;
        NativeMethods.DwmSetWindowAttribute(
            hwnd, NativeMethods.DWMWA_BORDER_COLOR, ref surface, sizeof(int));
    }

    /// <summary>
    /// Paints the rim an explicit color, for a window whose surface the caller knows better than
    /// this class does. Only the dock uses it.
    /// <para>
    /// <see cref="HideWindowBorder"/>'s constants are the right answer for a window whose surface
    /// <em>is</em> the theme's surface color, which the Mica dialogs are. The dock is not: its
    /// glass is translucent, so what it renders is its tint diluted by however much of the desktop
    /// the frostiness setting lets through, and light mode's #F3F3F3 drew a hard white outline
    /// around a dock that was actually rendering mid-grey. The dock therefore derives its own rim
    /// from the recipe its backdrop is running (see <c>DockWindow.ApplyWindowBorder</c>).
    /// </para>
    /// <para>
    /// And it must be <em>some</em> color: <c>DWMWA_COLOR_NONE</c> is no more "draw nothing" on the
    /// dock than it is on the dialogs. Tried and measured on the dock specifically, on the theory
    /// that <see cref="StripFrame"/> leaves no frame for the sentinel to paint — it removes the
    /// left, right and bottom edges and then draws the top one flat white in light mode, which is
    /// the same failure the dialogs saw and is worse than a rim that merely doesn't match.
    /// </para>
    /// </summary>
    public static void SetWindowBorderColor(nint hwnd, bool dark, Windows.UI.Color color)
    {
        SetFrameTheme(hwnd, dark);

        // DWM wants a COLORREF: 0x00BBGGRR, the opposite byte order from the Color we are handed.
        int colorRef = color.R | (color.G << 8) | (color.B << 16);
        NativeMethods.DwmSetWindowAttribute(
            hwnd, NativeMethods.DWMWA_BORDER_COLOR, ref colorRef, sizeof(int));
    }

    /// <summary>
    /// Strips the non-client window frame (caption / resize border) so the client area — and
    /// our glass — extends all the way to the window edge. This removes the ~3px frame whose
    /// inner highlight shows up as a white line around a rounded backdrop window.
    /// </summary>
    public static void StripFrame(nint hwnd)
    {
        long style = NativeMethods.GetWindowLongPtr(hwnd, NativeMethods.GWL_STYLE);
        style &= ~(NativeMethods.WS_CAPTION | NativeMethods.WS_THICKFRAME |
                   NativeMethods.WS_BORDER | NativeMethods.WS_DLGFRAME);
        NativeMethods.SetWindowLongPtr(hwnd, NativeMethods.GWL_STYLE, (nint)style);
        NativeMethods.SetWindowPos(hwnd, 0, 0, 0, 0, 0,
            NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE |
            NativeMethods.SWP_NOZORDER | NativeMethods.SWP_NOACTIVATE |
            NativeMethods.SWP_FRAMECHANGED);
    }

    /// <summary>Re-asserts top-most Z-order without stealing activation.</summary>
    public static void EnsureTopmost(nint hwnd)
    {
        NativeMethods.SetWindowPos(
            hwnd, NativeMethods.HWND_TOPMOST, 0, 0, 0, 0,
            NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE |
            NativeMethods.SWP_NOACTIVATE | NativeMethods.SWP_SHOWWINDOW);
    }

    /// <summary>Drops the window out of the always-on-top band (it stacks normally again).</summary>
    public static void SetNotTopmost(nint hwnd)
    {
        NativeMethods.SetWindowPos(
            hwnd, NativeMethods.HWND_NOTOPMOST, 0, 0, 0, 0,
            NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE |
            NativeMethods.SWP_NOACTIVATE | NativeMethods.SWP_SHOWWINDOW);
    }
}
