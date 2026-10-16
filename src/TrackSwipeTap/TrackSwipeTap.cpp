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
#include <format>
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
                if (!isFrame && !isTrayIcon && !isIndicator && !isLabel && type.find(L"ToggleButton") == std::wstring_view::npos) return S_OK;
                EnsureControlWindow();
                Windows::Foundation::IInspectable object;
                if (FAILED(m_diagnostics->GetIInspectableFromHandle(element.Handle, reinterpret_cast<::IInspectable**>(put_abi(object))))) return S_OK;
                auto found = object.try_as<FrameworkElement>();
                if (!found) return S_OK;
                if (isFrame) AddFrame(found);
                else if (isTrayIcon) AddTrayIcon(found);
                else if (isIndicator) AddIndicator(found);
                else if (isLabel) { if (auto text = found.try_as<Controls::TextBlock>()) TrackText(text); }
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
                        if (!known) { m_slots.push_back(Watched<FrameworkElement>{ make_weak(slot) }); ApplySlot(m_slots.back()); }
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
        double ClockSpace() const { return m_iconSpace * 0.6; }

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
            if (!item.watched && m_textSize > 0)
            {
                item.watched = true;
                text.RegisterPropertyChangedCallback(Controls::TextBlock::FontSizeProperty(), [this](DependencyObject const& sender, DependencyProperty const&)
                {
                    try
                    {
                        if (auto t = sender.try_as<Controls::TextBlock>(); t && m_textSize > 0 && t.FontSize() != m_textSize) t.FontSize(m_textSize);
                    }
                    catch (...) {}
                });
            }
            if (m_textSize > 0) { if (text.FontSize() != m_textSize) text.FontSize(m_textSize); }
            else if (item.watched) text.ClearValue(Controls::TextBlock::FontSizeProperty());
            // App buttons lay their titles out 0.67 lower than the clock (measured at 14 px): lift them onto its line.
            Offset(item, 0, m_textSize > 0 ? m_textOffset - (text.Name() == L"LabelControl" ? 0.67 : 0) : 0);
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
            if (m_iconSpace > 0) { item.watched = true; slot.Margin({ m_iconSpace / 2, 0, m_iconSpace / 2, 0 }); }
            else if (item.watched) { item.watched = false; slot.ClearValue(FrameworkElement::MarginProperty()); }
        }

        // ---- Diagnostics --------------------------------------------------------------------------------

        static void DumpElement(DependencyObject const& node, UIElement const& frame, int depth, std::wstring& out)
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
                if (auto text = element.try_as<Controls::TextBlock>())
                {
                    line += std::format(L" font {:g} {}", text.FontSize(), std::wstring_view(text.FontFamily().Source()));
                    try { line += std::format(L" top {:.2f}", element.TransformToVisual(frame).TransformPoint({ 0, 0 }).Y); }
                    catch (...) {}
                    if (auto value = text.Text(); !value.empty() && IsIconFont(text)) line += std::format(L" glyph U+{:04X}", (unsigned)value[0]);
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
            // TrackSwipe may run as administrator; let its commands through.
            for (auto message : { TrackSwipeTap::StartButton, TrackSwipeTap::EdgeLine, TrackSwipeTap::Query, TrackSwipeTap::Dump, TrackSwipeTap::TrayIcons, TrackSwipeTap::Indicators, TrackSwipeTap::TrayText })
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
