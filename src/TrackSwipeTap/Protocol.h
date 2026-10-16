#pragma once
// Shared between TrackSwipe (src/TrackSwipe/Interop/TaskbarTap.cs mirrors these values) and TrackSwipeTap.dll.
#include <windows.h>

namespace TrackSwipeTap
{
    // {5C36979C-4B69-4AE1-9B76-9D4B249E5659}
    inline constexpr CLSID Clsid = { 0x5c36979c, 0x4b69, 0x4ae1, { 0x9b, 0x76, 0x9d, 0x4b, 0x24, 0x9e, 0x56, 0x59 } };

    // The DLL's message-only window inside Explorer, and the messages it accepts.
    // Versioned: TrackSwipe asks an older copy still loaded in Explorer to stand down before loading this one.
    inline constexpr wchar_t WindowClass[] = L"TrackSwipe.Tap.18";
    /// wParam 1 hides the Start button, 0 gives it back to Explorer. lParam: space to leave before the first app button
    /// while it's hidden, in hundredths of an effective pixel (so a left-aligned taskbar starts as far in as the clock ends).
    inline constexpr UINT StartButton = WM_APP + 1;
    /// wParam 1 hides the taskbar's edge line, 0 gives it back.
    inline constexpr UINT EdgeLine = WM_APP + 2;
    /// Returns MAKELRESULT(Start buttons tracked, flags): bit 0 Start hidden, bit 1 edge hidden, bits 2+ edge lines tracked.
    inline constexpr UINT Query = WM_APP + 3;
    /// Development aid: writes the taskbar's XAML tree to %LOCALAPPDATA%\TrackSwipe\taskbar-tree.txt.
    inline constexpr UINT Dump = WM_APP + 4;
    /// Larger system tray icons (the overflow chevron, network, volume, battery): wParam is their glyph size and lParam
    /// the space added beside each, both in hundredths of an effective pixel. wParam 0 gives them back to Explorer.
    inline constexpr UINT TrayIcons = WM_APP + 5;
    /// wParam 1 hides the running-app indicators under taskbar buttons, 0 gives them back.
    inline constexpr UINT Indicators = WM_APP + 6;
    /// Tray text (the clock, battery percentage) and app buttons' titles, to go with larger icons: wParam its size and lParam how far to move it
    /// down (negative: up), both in hundredths of an effective pixel. wParam 0 gives it back to Explorer.
    inline constexpr UINT TrayText = WM_APP + 7;
    /// App buttons' icons: wParam their size in hundredths of an effective pixel (sharp, from the shell's own icons),
    /// 0 gives them back to Explorer.
    inline constexpr UINT AppIcons = WM_APP + 8;
}
