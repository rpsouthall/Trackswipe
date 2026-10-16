#pragma once
// Shared between TrackSwipe (src/TrackSwipe/Interop/TaskbarTap.cs mirrors these values) and TrackSwipeTap.dll.
#include <windows.h>

namespace TrackSwipeTap
{
    // {5C36979C-4B69-4AE1-9B76-9D4B249E5659}
    inline constexpr CLSID Clsid = { 0x5c36979c, 0x4b69, 0x4ae1, { 0x9b, 0x76, 0x9d, 0x4b, 0x24, 0x9e, 0x56, 0x59 } };

    // The DLL's message-only window inside Explorer, and the messages it accepts.
    inline constexpr wchar_t WindowClass[] = L"TrackSwipe.Tap";
    /// wParam 1 hides the Start button, 0 gives it back to Explorer.
    inline constexpr UINT StartButton = WM_APP + 1;
    /// wParam 1 hides the taskbar's edge line, 0 gives it back.
    inline constexpr UINT EdgeLine = WM_APP + 2;
    /// Returns MAKELRESULT(Start buttons tracked, flags): bit 0 Start hidden, bit 1 edge hidden, bits 2+ edge lines tracked.
    inline constexpr UINT Query = WM_APP + 3;
    /// Development aid: writes the taskbar's XAML tree to %LOCALAPPDATA%\TrackSwipe\taskbar-tree.txt.
    inline constexpr UINT Dump = WM_APP + 4;
}
