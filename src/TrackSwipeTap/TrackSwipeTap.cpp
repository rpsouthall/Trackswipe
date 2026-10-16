// TrackSwipeTap.dll: loaded into explorer.exe through XAML Diagnostics (InitializeXamlDiagnosticsEx) when one of
// TrackSwipe's taskbar options is on. It watches the taskbar's XAML tree and can
//   - collapse the Start button (automation ID "StartButton") and keep it collapsed;
//   - collapse the taskbar's edge line (BackgroundStroke), the hairline where it meets the desktop;
//   - collapse the running-app indicators under taskbar buttons;
//   - enlarge the system tray's icons (glyphs from Windows' icon font) and space them out, keeping them that way.
// A message-only window (Protocol.h's WindowClass) on the taskbar's UI thread takes commands from TrackSwipe.exe.
// Adapted from HideStart.
//
// This code runs inside Explorer: every entry point catches everything, so a failure leaves the taskbar as it was.

#define NOMINMAX
#include <unknwn.h>
#include <windows.h>
#include <ocidl.h>
#include <xamlom.h>
#undef GetCurrentTime // winbase.h macro; clashes with a XAML method name
#include <winrt/base.h>
#include <winrt/Windows.Foundation.h>
#include <winrt/Windows.UI.Xaml.h>
#include <winrt/Windows.UI.Xaml.Automation.h>
#include <winrt/Windows.UI.Xaml.Controls.h>
#include <winrt/Windows.UI.Xaml.Media.h>
#include <winrt/Windows.UI.Xaml.Media.Imaging.h>
#include <winrt/Windows.Storage.Streams.h>
#include <winrt/Windows.UI.ViewManagement.h>
#include <shellapi.h> // SHGetPropertyStoreForWindow (left out by WIN32_LEAN_AND_MEAN)
#include <shlobj.h>
#include <shobjidl.h>
#include <format>
#include <limits>
#include <map>
#include <memory>
#include <set>
#include <string>
#include <string_view>
#include <thread>
#include <vector>

#include "Protocol.h"

using namespace winrt;
using namespace winrt::Windows::UI::Xaml;

namespace
{
    HMODULE g_module;

    FrameworkElement FindByName(DependencyObject const& root, std::wstring_view name, int depth = 0)
    {
        if (auto element = root.try_as<FrameworkElement>(); element && element.Name() == name) return element;
        if (depth > 40) return nullptr;
        auto count = Media::VisualTreeHelper::GetChildrenCount(root);
        for (int i = 0; i < count; ++i)
            if (auto found = FindByName(Media::VisualTreeHelper::GetChild(root, i), name, depth + 1)) return found;
        return nullptr;
    }

    /// Appends a line to %LOCALAPPDATA%\TrackSwipe\taskbar-helper.log: only for failures, so it stays empty normally.
    void Note(std::wstring const& text)
    {
        wchar_t folder[MAX_PATH];
        if (!GetEnvironmentVariableW(L"LOCALAPPDATA", folder, MAX_PATH)) return;
        auto file = CreateFileW((std::wstring(folder) + L"\\TrackSwipe\\taskbar-helper.log").c_str(), FILE_APPEND_DATA, FILE_SHARE_READ | FILE_SHARE_WRITE, nullptr, OPEN_ALWAYS, 0, nullptr);
        if (file == INVALID_HANDLE_VALUE) return;
        SYSTEMTIME now; GetLocalTime(&now);
        auto line = std::format(L"{:02}:{:02}:{:02} {}: {}\r\n", now.wHour, now.wMinute, now.wSecond, TrackSwipeTap::WindowClass, text);
        auto size = WideCharToMultiByte(CP_UTF8, 0, line.c_str(), (int)line.size(), nullptr, 0, nullptr, nullptr);
        std::string utf8(size, '\0');
        WideCharToMultiByte(CP_UTF8, 0, line.c_str(), (int)line.size(), utf8.data(), size, nullptr, nullptr);
        DWORD written; WriteFile(file, utf8.data(), (DWORD)utf8.size(), &written, nullptr);
        CloseHandle(file);
    }

    // ---- Shell icons (off the UI thread) ------------------------------------------------------------------

    /// Posted to the control window when a shell icon has loaded: lParam is an IconPixels it takes over.
    constexpr UINT IconReady = WM_APP + 40;
    /// Posted to the control window when the system's Text size setting changes.
    constexpr UINT TextScaleChanged = WM_APP + 41;

    struct IconPixels
    {
        std::wstring key; // the taskbar button's automation ID
        int size = 0;     // requested, in pixels
        int width = 0, height = 0;
        std::vector<uint8_t> bgra; // premultiplied, top-down; empty if there's no icon
    };

    // {1E87508D-89C2-42F0-8A7E-645A0F50CA58}: the Apps folder, where every app is listed by its AppUserModelID.
    constexpr GUID AppsFolder = { 0x1e87508d, 0x89c2, 0x42f0, { 0x8a, 0x7e, 0x64, 0x5a, 0x0f, 0x50, 0xca, 0x58 } };
    // PKEY_AppUserModel_ID
    constexpr PROPERTYKEY AppUserModelId = { { 0x9f4c2855, 0x9f79, 0x4b39, { 0xa8, 0xd0, 0xe1, 0xd4, 0x2d, 0xe1, 0xd5, 0xf3 } }, 5 };

    com_ptr<IShellItemImageFactory> FromAppId(std::wstring const& id)
    {
        com_ptr<IShellItem> item;
        if (FAILED(SHCreateItemInKnownFolder(AppsFolder, 0, id.c_str(), IID_PPV_ARGS(item.put())))) return nullptr;
        return item.try_as<IShellItemImageFactory>();
    }

    std::wstring AppIdOf(HWND window)
    {
        std::wstring id;
        com_ptr<IPropertyStore> store;
        if (FAILED(SHGetPropertyStoreForWindow(window, IID_PPV_ARGS(store.put())))) return id;
        PROPVARIANT value;
        PropVariantInit(&value);
        if (SUCCEEDED(store->GetValue(AppUserModelId, &value)) && value.vt == VT_LPWSTR && value.pwszVal) id = value.pwszVal;
        PropVariantClear(&value);
        return id;
    }

    com_ptr<IShellItemImageFactory> FromProcessOf(HWND window)
    {
        DWORD pid = 0;
        GetWindowThreadProcessId(window, &pid);
        auto process = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, FALSE, pid);
        if (!process) return nullptr;
        wchar_t path[1024];
        DWORD length = ARRAYSIZE(path);
        com_ptr<IShellItemImageFactory> factory;
        com_ptr<IShellItem> item;
        if (QueryFullProcessImageNameW(process, 0, path, &length) && SUCCEEDED(SHCreateItemFromParsingName(path, nullptr, IID_PPV_ARGS(item.put()))))
            factory = item.try_as<IShellItemImageFactory>();
        CloseHandle(process);
        return factory;
    }

    /// The shell's image for a taskbar button ("Appid: <AppUserModelID>" or "Window: 0x<handle>"): the app as the
    /// Apps folder lists it, else the program the window belongs to.
    com_ptr<IShellItemImageFactory> IconSource(std::wstring const& key)
    {
        if (key.starts_with(L"Window: "))
        {
            auto window = reinterpret_cast<HWND>(static_cast<intptr_t>(wcstoull(key.c_str() + 8, nullptr, 16)));
            if (auto id = AppIdOf(window); !id.empty())
                if (auto factory = FromAppId(id)) return factory;
            return FromProcessOf(window);
        }
        if (!key.starts_with(L"Appid: ")) return nullptr;
        auto id = key.substr(7);
        if (auto factory = FromAppId(id)) return factory;
        // An ID the Apps folder doesn't list (a browser profile, say): the program of an open window that has it.
        struct Search { std::wstring const* id; HWND found; } search{ &id, nullptr };
        EnumWindows([](HWND window, LPARAM data) -> BOOL
        {
            auto s = reinterpret_cast<Search*>(data);
            if (IsWindowVisible(window) && AppIdOf(window) == *s->id) { s->found = window; return FALSE; }
            return TRUE;
        }, reinterpret_cast<LPARAM>(&search));
        return search.found ? FromProcessOf(search.found) : nullptr;
    }

    IconPixels LoadShellIcon(std::wstring const& key, int size)
    {
        IconPixels result{ key, size };
        auto factory = IconSource(key);
        HBITMAP bitmap = nullptr;
        if (!factory || FAILED(factory->GetImage({ size, size }, SIIGBF_ICONONLY, &bitmap)) || !bitmap) return result;
        BITMAP info{};
        GetObjectW(bitmap, sizeof(info), &info);
        int width = info.bmWidth, height = std::abs(info.bmHeight);
        std::vector<uint8_t> pixels(static_cast<size_t>(width) * height * 4);
        BITMAPINFO format{};
        format.bmiHeader = { sizeof(BITMAPINFOHEADER), width, -height, 1, 32, BI_RGB };
        auto dc = GetDC(nullptr);
        auto rows = GetDIBits(dc, bitmap, 0, height, pixels.data(), &format, DIB_RGB_COLORS);
        ReleaseDC(nullptr, dc);
        DeleteObject(bitmap);
        if (rows != height) return result;
        // XAML wants premultiplied alpha. The shell's bitmaps usually are already; if any colour exceeds its alpha, it isn't.
        auto premultiplied = true;
        for (size_t i = 0; i < pixels.size() && premultiplied; i += 4)
            premultiplied = pixels[i] <= pixels[i + 3] && pixels[i + 1] <= pixels[i + 3] && pixels[i + 2] <= pixels[i + 3];
        if (!premultiplied)
            for (size_t i = 0; i < pixels.size(); i += 4)
                for (int c = 0; c < 3; ++c) pixels[i + c] = static_cast<uint8_t>(pixels[i + c] * pixels[i + 3] / 255);
        result.width = width;
        result.height = height;
        result.bgra = std::move(pixels);
        return result;
    }

    DependencyObject TopOf(DependencyObject node)
    {
        while (auto parent = Media::VisualTreeHelper::GetParent(node)) node = parent;
        return node;
    }

    enum class Part { Start, Edge, Indicator };

    /// An element kept collapsed while its option is on, even if Explorer makes it visible again.
    struct Collapsible
    {
        weak_ref<FrameworkElement> element;
        Part part = Part::Start;
        bool watched = false; // its visibility callback is registered
    };

    /// An element whose property TrackSwipe sets, with a callback that keeps it set once registered.
    template <typename T> struct Watched
    {
        weak_ref<T> element;
        bool watched = false;
        bool moved = false; // TrackSwipe has given it a render offset
        bool grouped = false; // a tray slot that shares its button with others
        bool widened = false; // TrackSwipe has lifted its MaxWidth, saved here
        Windows::Foundation::IInspectable maxWidth{ nullptr };
    };

    struct Tap : implements<Tap, IObjectWithSite, IVisualTreeServiceCallback2>
    {
        // IObjectWithSite: XAML hands us its diagnostics interface here.
        HRESULT STDMETHODCALLTYPE SetSite(IUnknown* site) noexcept override
        {
            try
            {
                m_site.copy_from(site);
                m_diagnostics = nullptr;
                if (!site) return S_OK;
                m_diagnostics = m_site.as<IXamlDiagnostics>();
                // Advising from this thread hangs: XAML replays the existing tree on the UI thread, which is
                // waiting for this call. Advise from another thread; the callbacks still arrive on the UI thread.
                std::thread([self = get_strong()]
                {
                    try
                    {
                        auto hr = self->m_diagnostics.as<IVisualTreeService3>()->AdviseVisualTreeChange(self.get());
                        if (FAILED(hr)) Note(std::format(L"watching the taskbar failed: 0x{:08X}", (unsigned)hr));
                    }
                    catch (hresult_error const& e) { Note(std::format(L"watching the taskbar failed: 0x{:08X}", (unsigned)e.code().value)); }
                    catch (...) { Note(L"watching the taskbar failed"); }
                }).detach();
                return S_OK;
            }
            catch (...) { return to_hresult(); }
        }

        HRESULT STDMETHODCALLTYPE GetSite(REFIID riid, void** site) noexcept override
        {
            return m_site ? m_site->QueryInterface(riid, site) : E_FAIL;
        }

        // IVisualTreeServiceCallback: every element added to (or removed from) a XAML tree in Explorer.
        HRESULT STDMETHODCALLTYPE OnVisualTreeChange(ParentChildRelation, VisualElement element, VisualMutationType mutation) noexcept override
        {
            try
            {
                if (mutation != Add || !element.Type) return S_OK;
                std::wstring_view type(element.Type);
                // Cheap filter first: the Start button is one of the taskbar's toggle buttons; the frame is a taskbar's
                // root; a tray icon (new, or rebuilt when its state changes) is an IconView; each app button has a
                // RunningIndicator.
                auto isFrame = type == L"Taskbar.TaskbarFrame";
                auto isTrayIcon = IsIconViewType(type);
                auto isIndicator = element.Name && std::wstring_view(element.Name) == L"RunningIndicator";
                auto isLabel = element.Name && std::wstring_view(element.Name) == L"LabelControl"; // an app button's title
                auto isAppIcon = type == L"Windows.UI.Xaml.Controls.Image" && element.Name && std::wstring_view(element.Name) == L"Icon";
                if (!isFrame && !isTrayIcon && !isIndicator && !isLabel && !isAppIcon && type.find(L"ToggleButton") == std::wstring_view::npos) return S_OK;
                EnsureControlWindow();
                Windows::Foundation::IInspectable object;
                if (FAILED(m_diagnostics->GetIInspectableFromHandle(element.Handle, reinterpret_cast<::IInspectable**>(put_abi(object))))) return S_OK;
                auto found = object.try_as<FrameworkElement>();
                if (!found) return S_OK;
                if (isFrame) AddFrame(found);
                else if (isTrayIcon) AddTrayIcon(found);
                else if (isIndicator) AddIndicator(found);
                else if (isLabel) { if (auto text = found.try_as<Controls::TextBlock>()) TrackText(text); }
                else if (isAppIcon) { if (auto image = found.try_as<Controls::Image>()) AddAppIcon(image); }
                else ConsiderButton(found);
            }
            catch (...) {}
            return S_OK;
        }

        HRESULT STDMETHODCALLTYPE OnElementStateChanged(InstanceHandle, VisualElementState, LPCWSTR) noexcept override { return S_OK; }

        void SetStartHidden(bool hidden, double padding)
        {
            m_startHidden = hidden;
            m_padding = hidden ? padding : 0;
            for (auto& item : m_buttons) Apply(item, m_startHidden);
            for (auto& frame : m_frames) Pad(frame);
        }

        void SetEdgeHidden(bool hidden)
        {
            m_edgeHidden = hidden;
            for (auto& frame : m_frames) FindEdge(frame);
            for (auto& item : m_edges) Apply(item, m_edgeHidden);
        }

        void SetIndicatorsHidden(bool hidden)
        {
            m_indicatorsHidden = hidden;
            for (auto& frame : m_frames)
                if (auto f = frame.get()) FindIndicators(TopOf(f));
            std::erase_if(m_indicators, [](auto& item) { return !item.element.get(); });
            for (auto& item : m_indicators) Apply(item, m_indicatorsHidden);
        }

        void SetTrayIcons(double size, double space)
        {
            m_iconSize = size;
            m_iconSpace = size > 0 ? space : 0;
            for (auto& frame : m_frames)
                if (auto f = frame.get())
                    if (auto tray = FindByName(TopOf(f), L"SystemTrayFrameGrid")) ScaleTray(tray);
            for (auto& item : m_glyphs) ApplyGlyph(item);
            for (auto& item : m_slots) ApplySlot(item);
            for (auto& item : m_clockSlots) ApplyClockSlot(item);
        }

        /// Tray text and app buttons' titles, so all the taskbar's text matches the clock.
        void SetTrayText(double size, double offset)
        {
            m_textSize = size;
            m_textOffset = size > 0 ? offset : 0;
            for (auto& frame : m_frames)
                if (auto f = frame.get())
                {
                    auto top = TopOf(f);
                    if (auto tray = FindByName(top, L"SystemTrayFrameGrid")) ScaleTray(tray);
                    FindLabels(top);
                }
            for (auto& item : m_texts) ApplyText(item);
        }

        /// MAKELRESULT(Start buttons tracked, flags): bit 0 Start hidden, bit 1 edge hidden, bits 2+ edge lines tracked.
        LRESULT Query()
        {
            auto buttons = 0, edges = 0;
            for (auto& item : m_buttons) if (item.element.get()) ++buttons;
            for (auto& item : m_edges) if (item.element.get()) ++edges;
            return MAKELRESULT(buttons, (m_startHidden ? 1 : 0) | (m_edgeHidden ? 2 : 0) | (edges << 2));
        }

        /// Development aid: writes each taskbar's XAML tree to %LOCALAPPDATA%\TrackSwipe\taskbar-tree.txt.
        void DumpTrees()
        {
            std::wstring out;
            for (auto& weak : m_frames)
            {
                auto frame = weak.get();
                if (!frame) continue;
                out += L"=== taskbar\n";
                DumpElement(TopOf(frame), frame, 0, out);
            }
            wchar_t folder[MAX_PATH];
            if (!GetEnvironmentVariableW(L"LOCALAPPDATA", folder, MAX_PATH)) return;
            CreateDirectoryW((std::wstring(folder) + L"\\TrackSwipe").c_str(), nullptr);
            auto size = WideCharToMultiByte(CP_UTF8, 0, out.c_str(), (int)out.size(), nullptr, 0, nullptr, nullptr);
            std::string utf8(size, '\0');
            WideCharToMultiByte(CP_UTF8, 0, out.c_str(), (int)out.size(), utf8.data(), size, nullptr, nullptr);
            auto file = CreateFileW((std::wstring(folder) + L"\\TrackSwipe\\taskbar-tree.txt").c_str(), GENERIC_WRITE, FILE_SHARE_READ, nullptr, CREATE_ALWAYS, 0, nullptr);
            if (file == INVALID_HANDLE_VALUE) return;
            DWORD written; WriteFile(file, utf8.data(), (DWORD)utf8.size(), &written, nullptr);
            CloseHandle(file);
        }

    private:
        bool Hidden(Part part) const
        {
            return part == Part::Edge ? m_edgeHidden : part == Part::Indicator ? m_indicatorsHidden : m_startHidden;
        }

        /// Collapses or restores one tracked element, watching it the first time so Explorer can't bring it back.
        void Apply(Collapsible& item, bool hidden)
        {
            auto element = item.element.get();
            if (!element) return;
            if (!item.watched)
            {
                item.watched = true;
                element.RegisterPropertyChangedCallback(UIElement::VisibilityProperty(), [this, part = item.part](DependencyObject const& sender, DependencyProperty const&)
                {
                    try
                    {
                        if (auto e = sender.try_as<UIElement>(); e && Hidden(part) && e.Visibility() != Visibility::Collapsed)
                            e.Visibility(Visibility::Collapsed);
                    }
                    catch (...) {}
                });
            }
            if (hidden) element.Visibility(Visibility::Collapsed);
            else element.ClearValue(UIElement::VisibilityProperty()); // back to whatever Explorer chooses
        }

        static void Track(std::vector<Collapsible>& list, FrameworkElement const& element, Part part)
        {
            for (auto& item : list) if (item.element.get() == element) return;
            list.push_back(Collapsible{ make_weak(element), part });
        }

        // ---- Start button -------------------------------------------------------------------------------

        static bool IsStartButton(FrameworkElement const& element)
        {
            return Automation::AutomationProperties::GetAutomationId(element) == L"StartButton";
        }

        void ConsiderButton(FrameworkElement const& element)
        {
            if (IsStartButton(element)) { TrackButton(element); return; }
            // The automation ID may only be set once the button is loaded: look again then.
            element.Loaded([this, weak = make_weak(element)](auto&&, auto&&)
            {
                try { if (auto button = weak.get(); button && IsStartButton(button)) TrackButton(button); }
                catch (...) {}
            });
        }

        void TrackButton(FrameworkElement const& element)
        {
            Track(m_buttons, element, Part::Start);
            for (auto& item : m_buttons) if (item.element.get() == element) Apply(item, m_startHidden);
        }

        // ---- Edge line ----------------------------------------------------------------------------------

        void AddFrame(FrameworkElement const& frame)
        {
            for (auto& weak : m_frames) if (weak.get() == frame) return;
            m_frames.push_back(make_weak(frame));
            // Its parts may not exist yet: look once it has loaded too.
            frame.Loaded([this, weak = make_weak(frame)](auto&&, auto&&)
            {
                try { FindEdge(weak); for (auto& item : m_edges) Apply(item, m_edgeHidden); Pad(weak); }
                catch (...) {}
            });
            FindEdge(m_frames.back());
            for (auto& item : m_edges) Apply(item, m_edgeHidden);
            Pad(m_frames.back());
        }

        /// With the Start button hidden, leaves space before the first app button (a left margin on the button strip);
        /// otherwise gives the strip back its own layout.
        void Pad(weak_ref<FrameworkElement> const& weak)
        {
            auto frame = weak.get();
            if (!frame) return;
            auto repeater = FindByName(frame, L"TaskbarFrameRepeater");
            if (!repeater) return;
            if (m_padding > 0)
            {
                auto margin = repeater.Margin();
                if (margin.Left != m_padding) { margin.Left = m_padding; repeater.Margin(margin); }
            }
            else repeater.ClearValue(FrameworkElement::MarginProperty());
        }

        void FindEdge(weak_ref<FrameworkElement> const& weak)
        {
            auto frame = weak.get();
            if (!frame) return;
            auto background = FindByName(frame, L"BackgroundControl");
            if (auto stroke = background ? FindByName(background, L"BackgroundStroke") : nullptr) Track(m_edges, stroke, Part::Edge);
        }

        // ---- Running indicators -------------------------------------------------------------------------
        // The short line under an open app's button (longer and coloured for the active one). With labels shown, an
        // app's label and highlighted button already say as much.

        void AddIndicator(FrameworkElement const& indicator)
        {
            Track(m_indicators, indicator, Part::Indicator);
            for (auto& item : m_indicators) if (item.element.get() == indicator) Apply(item, m_indicatorsHidden);
        }

        void FindIndicators(DependencyObject const& root, int depth = 0)
        {
            if (auto element = root.try_as<FrameworkElement>(); element && element.Name() == L"RunningIndicator")
            {
                Track(m_indicators, element, Part::Indicator);
                return;
            }
            if (depth > 60) return;
            auto count = Media::VisualTreeHelper::GetChildrenCount(root);
            for (int i = 0; i < count; ++i) FindIndicators(Media::VisualTreeHelper::GetChild(root, i), depth + 1);
        }

        // ---- Tray icons ---------------------------------------------------------------------------------
        // The chevron, network, volume and battery icons are characters in Windows' icon font, each in its own
        // IconView slot in a row that sizes itself. A bigger font size enlarges a glyph (it stays sharp); a margin on
        // its slot spaces the row out. The clock, battery percentage and any other tray text are ordinary text, sized
        // separately so they stay in proportion with the icons. Render offsets (which move nothing else) put each
        // on the same centre line.

        static bool IsIconFont(Controls::TextBlock const& text)
        {
            std::wstring_view family = text.FontFamily().Source();
            return family.find(L"Icons") != std::wstring_view::npos || family.find(L"MDL2") != std::wstring_view::npos;
        }

        /// A tray icon's slot: IconView, or a variant such as the overflow chevron's ChevronIconView.
        static bool IsIconViewType(std::wstring_view type)
        {
            return type.starts_with(L"SystemTray.") && type.ends_with(L"IconView");
        }

        static bool IsIconView(DependencyObject const& node) { return IsIconViewType(get_class_name(node)); }

        static bool InQuickSettings(DependencyObject node)
        {
            for (auto depth = 0; node && depth < 12; ++depth, node = Media::VisualTreeHelper::GetParent(node))
                if (auto e = node.try_as<FrameworkElement>(); e && e.Name() == L"ControlCenterButton") return true;
            return false;
        }

        static FrameworkElement SlotOf(DependencyObject node)
        {
            for (auto depth = 0; node && depth < 12; ++depth, node = Media::VisualTreeHelper::GetParent(node))
                if (IsIconView(node)) return node.as<FrameworkElement>();
            return nullptr;
        }

        void AddTrayIcon(FrameworkElement const& icon)
        {
            // Its glyphs come with its template: look once it has loaded.
            icon.Loaded([this, weak = make_weak(icon)](auto&&, auto&&)
            {
                try { if (auto i = weak.get(); i && (m_iconSize > 0 || m_textSize > 0)) ScaleTray(i); }
                catch (...) {}
            });
        }

        /// Starts tracking every icon glyph and piece of text under root (the tray, or one new icon), and glyphs' slots.
        void ScaleTray(DependencyObject const& root, int depth = 0)
        {
            if (auto text = root.try_as<Controls::TextBlock>(); text && !IsIconFont(text))
            {
                // The InnerTextBlocks are layers of icons (empty ones fall back to a text font); the rest, such as the
                // clock and battery percentage, are text, tracked even while empty or hidden so they're ready to show.
                if (text.Name() == L"InnerTextBlock") return;
                TrackText(text);
                if (text.Name() == L"TimeInnerTextBlock")
                    if (auto slot = SlotOf(text))
                    {
                        auto known = false;
                        for (auto& item : m_clockSlots) if (item.element.get() == slot) { known = true; ApplyClockSlot(item); }
                        if (!known) { m_clockSlots.push_back(Watched<FrameworkElement>{ make_weak(slot) }); ApplyClockSlot(m_clockSlots.back()); }
                    }
                return;
            }
            if (auto text = root.try_as<Controls::TextBlock>(); text && IsIconFont(text))
            {
                auto tracked = false;
                for (auto& item : m_glyphs) if (item.element.get() == text) { tracked = true; ApplyGlyph(item); }
                if (!tracked) { m_glyphs.push_back(Watched<Controls::TextBlock>{ make_weak(text) }); ApplyGlyph(m_glyphs.back()); }
                // Only slots that show a glyph: the show-desktop strip has empty ones and keeps its width.
                if (!text.Text().empty())
                    if (auto slot = SlotOf(text))
                    {
                        auto known = false;
                        for (auto& item : m_slots) if (item.element.get() == slot) { known = true; ApplySlot(item); }
                        if (!known)
                        {
                            m_slots.push_back(Watched<FrameworkElement>{ make_weak(slot) });
                            m_slots.back().grouped = InQuickSettings(slot);
                            ApplySlot(m_slots.back());
                        }
                    }
                return;
            }
            if (depth > 60) return;
            auto count = Media::VisualTreeHelper::GetChildrenCount(root);
            for (int i = 0; i < count; ++i) ScaleTray(Media::VisualTreeHelper::GetChild(root, i), depth + 1);
            // Prune forgotten elements now and then.
            if (depth == 0)
            {
                std::erase_if(m_glyphs, [](auto& item) { return !item.element.get(); });
                std::erase_if(m_slots, [](auto& item) { return !item.element.get(); });
                std::erase_if(m_texts, [](auto& item) { return !item.element.get(); });
                std::erase_if(m_clockSlots, [](auto& item) { return !item.element.get(); });
            }
        }

        void ApplyGlyph(Watched<Controls::TextBlock>& item)
        {
            auto text = item.element.get();
            if (!text) return;
            if (!item.watched && m_iconSize > 0)
            {
                item.watched = true;
                // Explorer sets some glyphs' size again as their state changes: put ours back.
                text.RegisterPropertyChangedCallback(Controls::TextBlock::FontSizeProperty(), [this](DependencyObject const& sender, DependencyProperty const&)
                {
                    try
                    {
                        if (auto t = sender.try_as<Controls::TextBlock>(); t && m_iconSize > 0 && t.FontSize() != m_iconSize) t.FontSize(m_iconSize);
                    }
                    catch (...) {}
                });
            }
            if (m_iconSize > 0) { if (text.FontSize() != m_iconSize) text.FontSize(m_iconSize); }
            else if (item.watched) text.ClearValue(Controls::TextBlock::FontSizeProperty());
            Offset(item, 0, m_iconSize > 0 ? m_iconSize * CentreCorrection(text) : 0);
        }

        /// Extra space before the clock (a left margin on its slot): the battery's slot has little padding on its
        /// right, so at 18 px it stood 15 px from the clock, against 20 between the icons (measured on screen).
        double ClockSpace() const { return m_iconSpace * 0.85; }

        /// How far (as a fraction of its size) a glyph is moved down so its ink centres on the same line as the
        /// others and the text: the overflow chevron draws below the middle of its box (measured on screen at 18 px:
        /// ink centred 0.5 px low).
        static double CentreCorrection(Controls::TextBlock const& text)
        {
            for (DependencyObject node = text; node; node = Media::VisualTreeHelper::GetParent(node))
            {
                auto type = get_class_name(node);
                if (type == L"SystemTray.ChevronIconView") return -0.028;
                if (IsIconViewType(type)) break;
            }
            return 0;
        }

        void TrackText(Controls::TextBlock const& text)
        {
            for (auto& item : m_texts) if (item.element.get() == text) { ApplyText(item); return; }
            m_texts.push_back(Watched<Controls::TextBlock>{ make_weak(text) });
            ApplyText(m_texts.back());
        }

        void FindLabels(DependencyObject const& root, int depth = 0)
        {
            if (auto text = root.try_as<Controls::TextBlock>(); text && text.Name() == L"LabelControl") { TrackText(text); return; }
            if (depth > 60) return;
            auto count = Media::VisualTreeHelper::GetChildrenCount(root);
            for (int i = 0; i < count; ++i) FindLabels(Media::VisualTreeHelper::GetChild(root, i), depth + 1);
        }

        void ApplyText(Watched<Controls::TextBlock>& item)
        {
            auto text = item.element.get();
            if (!text) return;
            if (!item.watched && TextSized())
            {
                item.watched = true;
                text.RegisterPropertyChangedCallback(Controls::TextBlock::FontSizeProperty(), [this](DependencyObject const& sender, DependencyProperty const&)
                {
                    try
                    {
                        if (auto t = sender.try_as<Controls::TextBlock>(); t && TextSized() && t.FontSize() != m_textSize) t.FontSize(m_textSize);
                    }
                    catch (...) {}
                });
            }
            if (TextSized()) { if (text.FontSize() != m_textSize) text.FontSize(m_textSize); }
            else if (item.watched) text.ClearValue(Controls::TextBlock::FontSizeProperty());
            auto label = text.Name() == L"LabelControl";
            // App buttons lay their titles out a little lower than the clock: lift them onto its line.
            Offset(item, 0, m_textSize > 0 ? m_textOffset - (label ? LabelLift : 0) : 0);
            if (label) Widen(item, WidenTitle(text));
        }

        /// How far app titles are lifted onto the clock's line (measured on screen).
        static constexpr double LabelLift = 0.67;

        /// Tray text and titles are given a size of their own, rather than following Windows' (and with it, the
        /// system's Text size setting).
        bool TextSized() const { return m_textSize > TrackSwipeTap::FollowWindows / 100.0; }

        /// The system's Text size setting (Settings, Accessibility), as a factor: 1 at 100%.
        double TextScale() const
        {
            try { return m_uiSettings ? m_uiSettings.TextScaleFactor() : 1.0; }
            catch (...) { return 1.0; }
        }

        /// Windows caps app titles at 136 px, sized for its own 12 px text at 100%: once a title is drawn larger (a
        /// size here above 12, or a Text size above 100% that the title follows), the cap comes off so it isn't cut short.
        bool WidenTitle(Controls::TextBlock const& text) const
        {
            if (m_textSize <= 0) return false;
            return TextSized() ? m_textSize > 12 : text.IsTextScaleFactorEnabled() && TextScale() > 1.0;
        }

        /// App titles are capped at 136 px (sized for 12 px text), which cuts larger titles short: lift the cap so
        /// each button hugs its title, and keep it lifted. Explorer's own value is put back afterwards.
        void Widen(Watched<Controls::TextBlock>& item, bool on)
        {
            auto text = item.element.get();
            if (!text) return;
            auto property = FrameworkElement::MaxWidthProperty();
            if (on)
            {
                if (!item.widened)
                {
                    item.maxWidth = text.ReadLocalValue(property);
                    item.widened = true;
                    text.RegisterPropertyChangedCallback(property, [this](DependencyObject const& sender, DependencyProperty const&)
                    {
                        try
                        {
                            if (auto t = sender.try_as<Controls::TextBlock>(); t && WidenTitle(t) && t.MaxWidth() < 1e300) t.MaxWidth(std::numeric_limits<double>::infinity());
                        }
                        catch (...) {}
                    });
                }
                if (text.MaxWidth() < 1e300) text.MaxWidth(std::numeric_limits<double>::infinity());
            }
            else if (item.widened)
            {
                item.widened = false; // the callback stays registered, but only acts while the text is enlarged
                if (item.maxWidth && item.maxWidth != DependencyProperty::UnsetValue()) text.SetValue(property, item.maxWidth);
                else text.ClearValue(property);
                item.maxWidth = nullptr;
            }
        }

        /// More space before the clock (a left margin on its slot), so the battery sits evenly between it and volume.
        void ApplyClockSlot(Watched<FrameworkElement>& item)
        {
            auto slot = item.element.get();
            if (!slot) return;
            if (m_iconSize > 0 && ClockSpace() > 0) { item.watched = true; slot.Margin({ ClockSpace(), 0, 0, 0 }); }
            else if (item.watched) { item.watched = false; slot.ClearValue(FrameworkElement::MarginProperty()); }
        }

        /// Moves an element by (x, y) without changing the layout, or gives back its own render transform at 0.
        static void Offset(Watched<Controls::TextBlock>& item, double x, double y)
        {
            auto text = item.element.get();
            if (!text) return;
            if (x != 0 || y != 0)
            {
                Media::TranslateTransform move;
                move.X(x);
                move.Y(y);
                text.RenderTransform(move);
                item.moved = true;
            }
            else if (item.moved)
            {
                text.ClearValue(UIElement::RenderTransformProperty());
                item.moved = false;
            }
        }

        void ApplySlot(Watched<FrameworkElement>& item)
        {
            auto slot = item.element.get();
            if (!slot) return;
            // Icons that share one button (network, volume and battery, in Quick Settings' button) sit closer
            // together than separate ones: half the space.
            auto space = item.grouped ? m_iconSpace / 2 : m_iconSpace;
            if (m_iconSpace > 0) { item.watched = true; slot.Margin({ space / 2, 0, space / 2, 0 }); }
            else if (item.watched) { item.watched = false; slot.ClearValue(FrameworkElement::MarginProperty()); }
        }

        // ---- App icons ----------------------------------------------------------------------------------
        // Each app button's icon is an Image showing a bitmap Explorer made at exactly its 16 px size, so it can't be
        // enlarged without blurring. Larger icons get their own bitmap from the shell (as Start and File Explorer's
        // big icons do) at the new size in pixels, loaded off the taskbar's thread; until it arrives, and if there's
        // none, Explorer's is shown enlarged. Explorer's bitmap and size are put back afterwards.

    public:
        void SetAppIcons(double size, double spacing)
        {
            m_appIconSize = size;
            m_appSpacing = size > 0 ? spacing : 0;
            for (auto& frame : m_frames)
                if (auto f = frame.get()) FindAppIcons(TopOf(f));
            std::erase_if(m_appIcons, [](auto& item) { return !item.image.get(); });
            for (auto& item : m_appIcons) ApplyAppIcon(item);
        }

        /// A shell icon has loaded (or turned out not to exist): make it a XAML bitmap and show it.
        void IconLoaded(IconPixels const& pixels)
        {
            auto key = std::make_pair(pixels.key, pixels.size);
            m_iconPending.erase(key);
            Media::ImageSource bitmap{ nullptr };
            if (!pixels.bgra.empty())
            {
                Media::Imaging::WriteableBitmap writeable(pixels.width, pixels.height);
                memcpy(writeable.PixelBuffer().data(), pixels.bgra.data(), pixels.bgra.size());
                writeable.Invalidate();
                bitmap = writeable;
            }
            m_iconCache.insert_or_assign(key, bitmap); // kept even when empty, so a missing icon isn't looked up again
            for (auto& item : m_appIcons)
            {
                if (auto image = item.image.get())
                    if (auto current = ButtonKeyOf(image); !current.empty()) item.key = current;
                if (item.key == pixels.key) ApplyAppIcon(item);
            }
        }

    private:
        struct AppIcon
        {
            weak_ref<Controls::Image> image;
            std::wstring key;   // its button's automation ID
            bool sized = false; // TrackSwipe has set its size; Explorer's own values saved here
            Windows::Foundation::IInspectable width{ nullptr }, height{ nullptr };
            Media::ImageSource original{ nullptr }; // Explorer's latest bitmap
            Media::ImageSource ours{ nullptr };     // the shell's, while shown
            bool watched = false;
            weak_ref<FrameworkElement> button; // its app button, spaced from its neighbours
            bool spaced = false;               // TrackSwipe has set the button's margin; Explorer's saved here
            Windows::Foundation::IInspectable margin{ nullptr };
        };

        /// Sets an icon's bitmap without its change callback taking it for Explorer's.
        void SetSource(Controls::Image const& image, Media::ImageSource const& source)
        {
            m_settingSource = true;
            try { image.Source(source); }
            catch (...) {}
            m_settingSource = false;
        }

        static FrameworkElement ButtonOf(DependencyObject node)
        {
            for (auto depth = 0; node && depth < 8; ++depth, node = Media::VisualTreeHelper::GetParent(node))
                if (get_class_name(node) == L"Taskbar.TaskListButton") return node.as<FrameworkElement>();
            return nullptr;
        }

        /// Space between app buttons: a margin either side, so each keeps its square highlight with a gap around it.
        void SpaceButton(AppIcon& item)
        {
            auto button = item.button.get();
            if (!button)
            {
                item.button = make_weak(button = ButtonOf(item.image.get()));
                if (!button) return;
                // Handed to another app (its automation ID changes): show that app's icon.
                button.RegisterPropertyChangedCallback(Automation::AutomationProperties::AutomationIdProperty(), [this](DependencyObject const& sender, DependencyProperty const&)
                {
                    try
                    {
                        if (m_appIconSize <= 0) return;
                        for (auto& other : m_appIcons)
                            if (auto b = other.button.get(); b && b == sender) { ApplyAppIcon(other); return; }
                    }
                    catch (...) {}
                });
            }
            if (m_appSpacing > 0)
            {
                if (!item.spaced) { item.margin = button.ReadLocalValue(FrameworkElement::MarginProperty()); item.spaced = true; }
                button.Margin({ m_appSpacing / 2, 0, m_appSpacing / 2, 0 });
            }
            else if (item.spaced)
            {
                item.spaced = false;
                if (item.margin && item.margin != DependencyProperty::UnsetValue()) button.SetValue(FrameworkElement::MarginProperty(), item.margin);
                else button.ClearValue(FrameworkElement::MarginProperty());
            }
        }

        static std::wstring ButtonKeyOf(DependencyObject node)
        {
            for (auto depth = 0; node && depth < 8; ++depth, node = Media::VisualTreeHelper::GetParent(node))
                if (get_class_name(node) == L"Taskbar.TaskListButton")
                    return std::wstring(Automation::AutomationProperties::GetAutomationId(node.as<UIElement>()));
            return {};
        }

        void AddAppIcon(Controls::Image const& image)
        {
            // Its button may not be its ancestor yet: look once it has loaded too.
            image.Loaded([this, weak = make_weak(image)](auto&&, auto&&)
            {
                try { if (auto i = weak.get(); i && m_appIconSize > 0) TrackAppIcon(i); }
                catch (...) {}
            });
            if (m_appIconSize > 0) TrackAppIcon(image);
        }

        void FindAppIcons(DependencyObject const& root, int depth = 0)
        {
            if (auto image = root.try_as<Controls::Image>(); image && image.Name() == L"Icon") { TrackAppIcon(image); return; }
            if (depth > 60) return;
            auto count = Media::VisualTreeHelper::GetChildrenCount(root);
            for (int i = 0; i < count; ++i) FindAppIcons(Media::VisualTreeHelper::GetChild(root, i), depth + 1);
        }

        void TrackAppIcon(Controls::Image const& image)
        {
            auto key = ButtonKeyOf(image);
            if (key.empty()) return; // not an app button's (the Start button's, say)
            for (auto& item : m_appIcons)
                if (item.image.get() == image) { item.key = key; ApplyAppIcon(item); return; }
            m_appIcons.push_back(AppIcon{ make_weak(image), key });
            ApplyAppIcon(m_appIcons.back());
        }

        int PixelsOf(Controls::Image const& image) const
        {
            auto scale = 1.0;
            try { if (auto root = image.XamlRoot()) scale = root.RasterizationScale(); }
            catch (...) {}
            return static_cast<int>(std::lround(m_appIconSize * scale));
        }

        void ApplyAppIcon(AppIcon& item)
        {
            auto image = item.image.get();
            if (!image) return;
            // Explorer reuses buttons for other apps, so which app this is is read every time.
            if (auto key = ButtonKeyOf(image); !key.empty()) item.key = key;
            if (!item.watched)
            {
                item.watched = true;
                // Explorer gives the button a new bitmap when the app's icon changes: remember it, and fetch the
                // shell's again in case that changed too.
                image.RegisterPropertyChangedCallback(Controls::Image::SourceProperty(), [this](DependencyObject const& sender, DependencyProperty const&)
                {
                    try
                    {
                        auto i = sender.try_as<Controls::Image>();
                        if (!i || m_settingSource) return; // ours, being set
                        for (auto& item : m_appIcons)
                        {
                            if (item.image.get() != i) continue;
                            item.original = i.Source();
                            item.ours = nullptr;
                            // Buttons are reused for other apps: only a new bitmap for the same app means its icon
                            // changed, and the shell's should be fetched again.
                            auto key = ButtonKeyOf(i);
                            if (!key.empty() && key == item.key) m_iconCache.erase(std::make_pair(item.key, PixelsOf(i)));
                            if (m_appIconSize > 0) ApplyAppIcon(item);
                            return;
                        }
                    }
                    catch (...) {}
                });
            }
            SpaceButton(item);
            if (m_appIconSize > 0)
            {
                if (!item.sized)
                {
                    item.width = image.ReadLocalValue(FrameworkElement::WidthProperty());
                    item.height = image.ReadLocalValue(FrameworkElement::HeightProperty());
                    item.sized = true;
                }
                image.Width(m_appIconSize);
                image.Height(m_appIconSize);
                auto key = std::make_pair(item.key, PixelsOf(image));
                auto cached = m_iconCache.find(key);
                Media::ImageSource wanted = cached != m_iconCache.end() ? cached->second : nullptr;
                if (wanted)
                {
                    if (image.Source() != wanted)
                    {
                        if (!item.ours) item.original = image.Source();
                        item.ours = wanted;
                        SetSource(image, wanted);
                    }
                }
                else if (item.ours)
                {
                    // Showing an icon fetched for the app this button had before: back to Explorer's own for now.
                    item.ours = nullptr;
                    SetSource(image, item.original);
                }
                if (cached == m_iconCache.end() && m_control && m_iconPending.insert(key).second)
                {
                    std::thread([control = m_control, key]
                    {
                        auto com = SUCCEEDED(CoInitializeEx(nullptr, COINIT_APARTMENTTHREADED | COINIT_DISABLE_OLE1DDE));
                        auto pixels = std::make_unique<IconPixels>();
                        try { *pixels = LoadShellIcon(key.first, key.second); }
                        catch (...) { pixels->key = key.first; pixels->size = key.second; }
                        if (com) CoUninitialize();
                        if (PostMessageW(control, IconReady, 0, reinterpret_cast<LPARAM>(pixels.get()))) pixels.release();
                    }).detach();
                }
            }
            else if (item.sized)
            {
                item.sized = false;
                auto restore = [&](DependencyProperty const& property, Windows::Foundation::IInspectable const& value)
                {
                    if (value && value != DependencyProperty::UnsetValue()) image.SetValue(property, value);
                    else image.ClearValue(property);
                };
                restore(FrameworkElement::WidthProperty(), item.width);
                restore(FrameworkElement::HeightProperty(), item.height);
                if (item.ours && image.Source() == item.ours && item.original) SetSource(image, item.original);
                item.ours = nullptr;
            }
        }

        // ---- Diagnostics --------------------------------------------------------------------------------

        void DumpElement(DependencyObject const& node, UIElement const& frame, int depth, std::wstring& out)
        {
            std::wstring line(depth * 2, L' ');
            line += get_class_name(node);
            if (auto element = node.try_as<FrameworkElement>())
            {
                if (auto name = element.Name(); !name.empty()) line += L" #" + std::wstring(name);
                if (auto id = Automation::AutomationProperties::GetAutomationId(element); !id.empty()) line += L" [" + std::wstring(id) + L"]";
                try
                {
                    auto at = element.TransformToVisual(frame).TransformPoint({ 0, 0 });
                    line += std::format(L" at {:.0f},{:.0f}", at.X, at.Y);
                }
                catch (...) {}
                line += std::format(L" size {:.0f}x{:.0f}", element.ActualWidth(), element.ActualHeight());
                if (element.Visibility() == Visibility::Collapsed) line += L" COLLAPSED";
                if (auto max = element.MaxWidth(); max < 1e6) line += std::format(L" maxw {:g}", max);
                if (auto width = element.Width(); width == width) line += std::format(L" w {:g}", width); // set (not NaN)
                if (auto text = element.try_as<Controls::TextBlock>())
                {
                    line += std::format(L" font {:g} {}", text.FontSize(), std::wstring_view(text.FontFamily().Source()));
                    if (!text.IsTextScaleFactorEnabled()) line += L" fixed"; // ignores the system's Text size
                    try { line += std::format(L" top {:.2f}", element.TransformToVisual(frame).TransformPoint({ 0, 0 }).Y); }
                    catch (...) {}
                    if (auto value = text.Text(); !value.empty() && IsIconFont(text)) line += std::format(L" glyph U+{:04X}", (unsigned)value[0]);
                }
                if (auto image = element.try_as<Controls::Image>())
                    if (auto source = image.Source())
                    {
                        line += std::format(L" source {}", std::wstring_view(get_class_name(source)));
                        if (auto bitmap = source.try_as<Media::Imaging::BitmapSource>()) line += std::format(L" {}x{}px", bitmap.PixelWidth(), bitmap.PixelHeight());
                        for (auto const& [key, cached] : m_iconCache)
                            if (cached && cached == source) { line += std::format(L" for [{}]", key.first); break; }
                        if (auto decoded = source.try_as<Media::Imaging::BitmapImage>())
                        {
                            line += std::format(L" decode {}x{}", decoded.DecodePixelWidth(), decoded.DecodePixelHeight());
                            if (auto uri = decoded.UriSource()) line += std::format(L" uri {}", std::wstring_view(uri.AbsoluteUri()));
                        }
                    }
            }
            out += line + L"\n";
            if (depth > 60) return;
            auto count = Media::VisualTreeHelper::GetChildrenCount(node);
            for (int i = 0; i < count; ++i) DumpElement(Media::VisualTreeHelper::GetChild(node, i), frame, depth + 1, out);
        }

        // ---- Control window -----------------------------------------------------------------------------

        // Created on the taskbar's UI thread (the first callback), so commands run where the elements live.
        void EnsureControlWindow()
        {
            if (m_control) return;
            WNDCLASSW wc{};
            wc.lpfnWndProc = ControlProc;
            wc.hInstance = g_module;
            wc.lpszClassName = TrackSwipeTap::WindowClass;
            RegisterClassW(&wc);
            m_control = CreateWindowExW(0, TrackSwipeTap::WindowClass, L"", 0, 0, 0, 0, 0, HWND_MESSAGE, nullptr, g_module, nullptr);
            if (!m_control) return;
            SetWindowLongPtrW(m_control, GWLP_USERDATA, reinterpret_cast<LONG_PTR>(this));
            try
            {
                // Text size changes arrive on another thread: hand them to this one.
                m_uiSettings = Windows::UI::ViewManagement::UISettings();
                m_uiSettings.TextScaleFactorChanged([control = m_control](auto&&, auto&&) { PostMessageW(control, TextScaleChanged, 0, 0); });
            }
            catch (...) {}
            // TrackSwipe may run as administrator; let its commands through.
            for (auto message : { TrackSwipeTap::StartButton, TrackSwipeTap::EdgeLine, TrackSwipeTap::Query, TrackSwipeTap::Dump, TrackSwipeTap::TrayIcons, TrackSwipeTap::Indicators, TrackSwipeTap::TrayText, TrackSwipeTap::AppIcons })
                ChangeWindowMessageFilterEx(m_control, message, MSGFLT_ALLOW, nullptr);
        }

        static LRESULT CALLBACK ControlProc(HWND window, UINT message, WPARAM wParam, LPARAM lParam)
        {
            try
            {
                if (auto tap = reinterpret_cast<Tap*>(GetWindowLongPtrW(window, GWLP_USERDATA)))
                {
                    switch (message)
                    {
                    case TrackSwipeTap::StartButton: tap->SetStartHidden(wParam != 0, lParam / 100.0); return 1;
                    case TrackSwipeTap::EdgeLine: tap->SetEdgeHidden(wParam != 0); return 1;
                    case TrackSwipeTap::Query: return tap->Query();
                    case TrackSwipeTap::Dump: tap->DumpTrees(); return 1;
                    case TrackSwipeTap::TrayIcons: tap->SetTrayIcons(wParam / 100.0, lParam / 100.0); return 1;
                    case TrackSwipeTap::TrayText: tap->SetTrayText(wParam / 100.0, lParam / 100.0); return 1;
                    case TrackSwipeTap::AppIcons: tap->SetAppIcons(wParam / 100.0, lParam / 100.0); return 1;
                    case TextScaleChanged: for (auto& item : tap->m_texts) tap->ApplyText(item); return 1;
                    case IconReady:
                    {
                        std::unique_ptr<IconPixels> pixels(reinterpret_cast<IconPixels*>(lParam));
                        tap->IconLoaded(*pixels);
                        return 1;
                    }
                    case TrackSwipeTap::Indicators: tap->SetIndicatorsHidden(wParam != 0); return 1;
                    }
                }
            }
            catch (...) {}
            return DefWindowProcW(window, message, wParam, lParam);
        }

        com_ptr<IUnknown> m_site;
        com_ptr<IXamlDiagnostics> m_diagnostics;
        std::vector<Collapsible> m_buttons;
        std::vector<Collapsible> m_edges;
        std::vector<Collapsible> m_indicators;
        std::vector<weak_ref<FrameworkElement>> m_frames;
        bool m_startHidden = false;
        bool m_edgeHidden = false;
        bool m_indicatorsHidden = false;
        std::vector<AppIcon> m_appIcons;
        double m_appIconSize = 0; // app button icons' size, 0 for Explorer's own
        double m_appSpacing = 0;  // added between app buttons
        Windows::UI::ViewManagement::UISettings m_uiSettings{ nullptr };
        bool m_settingSource = false; // an app icon's bitmap is being set by TrackSwipe
        std::map<std::pair<std::wstring, int>, Media::ImageSource> m_iconCache; // shell icons by button and pixel size
        std::set<std::pair<std::wstring, int>> m_iconPending;
        double m_padding = 0; // space before the first app button while Start is hidden
        std::vector<Watched<Controls::TextBlock>> m_glyphs; // tray icon glyphs
        std::vector<Watched<FrameworkElement>> m_slots;     // their IconViews
        double m_iconSize = 0;  // tray glyph size, 0 for Explorer's own
        double m_iconSpace = 0; // added beside each tray icon
        std::vector<Watched<Controls::TextBlock>> m_texts; // tray text: the clock, battery percentage
        std::vector<Watched<FrameworkElement>> m_clockSlots; // the clock's slot
        double m_textSize = 0;   // tray text size, 0 for Explorer's own
        double m_textOffset = 0; // tray text moved down by
        HWND m_control = nullptr;
    };

    // Kept for the life of Explorer: XAML holds our callback, and this DLL is never unloaded.
    com_ptr<Tap> g_tap;

    struct Factory : implements<Factory, IClassFactory>
    {
        HRESULT STDMETHODCALLTYPE CreateInstance(IUnknown* outer, REFIID riid, void** result) noexcept override
        {
            try
            {
                *result = nullptr;
                if (outer) return CLASS_E_NOAGGREGATION;
                if (!g_tap) g_tap = make_self<Tap>();
                return g_tap->QueryInterface(riid, result);
            }
            catch (...) { return to_hresult(); }
        }

        HRESULT STDMETHODCALLTYPE LockServer(BOOL) noexcept override { return S_OK; }
    };
}

BOOL APIENTRY DllMain(HMODULE module, DWORD reason, LPVOID)
{
    if (reason == DLL_PROCESS_ATTACH)
    {
        g_module = module;
        DisableThreadLibraryCalls(module);
    }
    return TRUE;
}

_Use_decl_annotations_ STDAPI DllGetClassObject(REFCLSID clsid, REFIID riid, LPVOID* result)
{
    try
    {
        *result = nullptr;
        if (clsid != TrackSwipeTap::Clsid) return CLASS_E_CLASSNOTAVAILABLE;
        return make<Factory>()->QueryInterface(riid, result);
    }
    catch (...) { return to_hresult(); }
}

_Use_decl_annotations_ STDAPI DllCanUnloadNow() { return S_FALSE; }
