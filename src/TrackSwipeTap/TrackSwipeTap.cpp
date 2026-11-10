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
#include <winrt/Windows.Foundation.Collections.h>
#include <winrt/Windows.UI.Xaml.h>
#include <winrt/Windows.UI.Xaml.Automation.h>
#include <winrt/Windows.UI.Xaml.Automation.Peers.h>
#include <winrt/Windows.UI.Xaml.Automation.Provider.h>
#include <winrt/Windows.UI.Xaml.Controls.h>
#include <winrt/Windows.UI.Xaml.Input.h>
#include <winrt/Windows.UI.Text.h>
#include <winrt/Windows.UI.h>
#include <winrt/Windows.UI.Xaml.Media.h>
#include <winrt/Windows.UI.Xaml.Media.Imaging.h>
#include <winrt/Windows.UI.Xaml.Media.Animation.h>
#include <winrt/Windows.Storage.Streams.h>
#include <winrt/Windows.UI.ViewManagement.h>
#include <shellapi.h> // SHGetPropertyStoreForWindow (left out by WIN32_LEAN_AND_MEAN)
#include <shlobj.h>
#include <shobjidl.h>
#include <appmodel.h>
#include <wincodec.h>
#include <format>
#include <limits>
#include <array>
#include <atomic>
#include <condition_variable>
#include <deque>
#include <fstream>
#include <map>
#include <regex>
#include <mutex>
#include <memory>
#include <set>
#include <string>
#include <string_view>
#include <thread>
#include <vector>

#include "Protocol.h"

#include <dwmapi.h>
#include <commctrl.h>
#pragma comment(lib, "comctl32.lib") // SetWindowSubclass: the minimize targets
#pragma comment(lib, "dwmapi.lib") // cloaked windows (other desktops) aren't the app in front
#pragma comment(lib, "version.lib") // the app name: a program's file description

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
    /// Posted to the control window when Windows' colours (light or dark mode among them) change.
    constexpr UINT ColorsChanged = WM_APP + 42;
    /// Posted to the control window when Explorer's list of virtual desktops, or which one is current, changes.
    constexpr UINT DesktopsChanged = WM_APP + 43;

    /// The taskbar is light (Settings, Personalisation, Colours: "Choose your mode").
    bool LightTaskbar()
    {
        DWORD value = 0, size = sizeof(value);
        return RegGetValueW(HKEY_CURRENT_USER, L"Software\\Microsoft\\Windows\\CurrentVersion\\Themes\\Personalize", L"SystemUsesLightTheme",
            RRF_RT_REG_DWORD, nullptr, &value, &size) == ERROR_SUCCESS && value != 0;
    }


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

    /// A Store app's own taskbar artwork for its AppUserModelID ("<family>!<app>") at px pixels, in the variant
    /// Explorer shows on a light or dark taskbar (altform-lightunplated or altform-unplated); empty if there's none.
    std::wstring PackageLogo(std::wstring const& appId, int px, bool light)
    {
        auto bang = appId.find(L'!');
        if (bang == std::wstring::npos) return {};
        auto family = appId.substr(0, bang), app = appId.substr(bang + 1);
        UINT32 count = 0, length = 0;
        if (GetPackagesByPackageFamily(family.c_str(), &count, nullptr, &length, nullptr) != ERROR_INSUFFICIENT_BUFFER || !count) return {};
        std::vector<PWSTR> names(count);
        std::vector<wchar_t> buffer(length);
        if (GetPackagesByPackageFamily(family.c_str(), &count, names.data(), &length, buffer.data()) != ERROR_SUCCESS) return {};
        wchar_t path[1024];
        UINT32 pathLength = ARRAYSIZE(path);
        if (GetPackagePathByFullName(names[0], &pathLength, path) != ERROR_SUCCESS) return {};
        std::wstring folder = path;
        // The app's Square44x44Logo in its manifest, e.g. Assets\AppList.png.
        std::ifstream file(folder + L"\\AppxManifest.xml", std::ios::binary);
        std::string manifest((std::istreambuf_iterator<char>(file)), std::istreambuf_iterator<char>());
        std::string narrowApp; // app IDs are ASCII
        for (auto ch : app) narrowApp += static_cast<char>(ch);
        auto at = manifest.find("Id=\"" + narrowApp + "\"");
        if (at == std::string::npos) at = 0;
        at = manifest.find("Square44x44Logo=\"", at);
        if (at == std::string::npos) return {};
        at += 17;
        auto end = manifest.find('"', at);
        if (end == std::string::npos) return {};
        std::wstring logo(manifest.begin() + at, manifest.begin() + end);
        std::replace(logo.begin(), logo.end(), L'/', L'\\');
        auto slash = logo.find_last_of(L'\\'), dot = logo.find_last_of(L'.');
        auto dir = folder + L"\\" + (slash == std::wstring::npos ? L"" : logo.substr(0, slash + 1));
        auto stem = logo.substr(slash == std::wstring::npos ? 0 : slash + 1, dot == std::wstring::npos ? std::wstring::npos : dot - (slash == std::wstring::npos ? 0 : slash + 1));
        // Pick among <stem>.targetsize-<n>[_altform-...].png: the theme's variant first, then the other unplated one,
        // then the plain one; among those, the smallest at least px, else the largest. High-contrast ones are skipped.
        const std::wregex pattern(L"\\.targetsize-(\\d+)(_altform-(lightunplated|unplated))?\\.png", std::regex::icase);
        std::wstring best;
        int bestRank = 99, bestSize = 0;
        WIN32_FIND_DATAW found;
        auto search = FindFirstFileW((dir + stem + L".targetsize-*.png").c_str(), &found);
        if (search == INVALID_HANDLE_VALUE) return {};
        do
        {
            std::wstring name = found.cFileName;
            std::wsmatch match;
            auto tail = name.substr(stem.size());
            if (!std::regex_match(tail, match, pattern)) continue;
            auto size = std::stoi(match[1].str());
            auto form = match[3].str();
            for (auto& ch : form) ch = towlower(ch);
            auto rank = form == (light ? L"lightunplated" : L"unplated") ? 0 : !form.empty() ? 1 : 2;
            auto better = rank < bestRank
                || (rank == bestRank && ((size >= px && (bestSize < px || size < bestSize)) || (size < px && bestSize < px && size > bestSize)));
            if (better) { best = dir + name; bestRank = rank; bestSize = size; }
        } while (FindNextFileW(search, &found));
        FindClose(search);
        return best;
    }

    /// Decodes an image file to px x px premultiplied BGRA, scaled to fit and centred.
    std::vector<uint8_t> DecodeImage(std::wstring const& path, int px)
    {
        com_ptr<IWICImagingFactory> wic;
        com_ptr<IWICBitmapDecoder> decoder;
        com_ptr<IWICBitmapFrameDecode> frame;
        com_ptr<IWICFormatConverter> converter;
        if (FAILED(CoCreateInstance(CLSID_WICImagingFactory, nullptr, CLSCTX_INPROC_SERVER, IID_PPV_ARGS(wic.put())))
            || FAILED(wic->CreateDecoderFromFilename(path.c_str(), nullptr, GENERIC_READ, WICDecodeMetadataCacheOnDemand, decoder.put()))
            || FAILED(decoder->GetFrame(0, frame.put())) || FAILED(wic->CreateFormatConverter(converter.put()))
            || FAILED(converter->Initialize(frame.get(), GUID_WICPixelFormat32bppPBGRA, WICBitmapDitherTypeNone, nullptr, 0, WICBitmapPaletteTypeCustom)))
            return {};
        UINT width = 0, height = 0;
        converter->GetSize(&width, &height);
        if (!width || !height) return {};
        auto scale = static_cast<double>(px) / std::max(width, height);
        UINT w = std::max(1u, static_cast<UINT>(std::lround(width * scale))), h = std::max(1u, static_cast<UINT>(std::lround(height * scale)));
        com_ptr<IWICBitmapSource> source = converter;
        com_ptr<IWICBitmapScaler> scaler;
        if (w != width || h != height)
        {
            if (FAILED(wic->CreateBitmapScaler(scaler.put())) || FAILED(scaler->Initialize(converter.get(), w, h, WICBitmapInterpolationModeHighQualityCubic))) return {};
            source = scaler;
        }
        std::vector<uint8_t> scaled(static_cast<size_t>(w) * h * 4);
        if (FAILED(source->CopyPixels(nullptr, w * 4, static_cast<UINT>(scaled.size()), scaled.data()))) return {};
        std::vector<uint8_t> pixels(static_cast<size_t>(px) * px * 4);
        auto left = (px - static_cast<int>(w)) / 2, top = (px - static_cast<int>(h)) / 2;
        for (UINT y = 0; y < h; ++y)
            memcpy(&pixels[((top + y) * px + left) * 4], &scaled[y * w * 4], w * 4);
        return pixels;
    }

    /// The app ID a taskbar button stands for: its own, or its window's.
    std::wstring AppIdOfKey(std::wstring const& key)
    {
        if (key.starts_with(L"Appid: ")) return key.substr(7);
        if (key.starts_with(L"Window: ")) return AppIdOf(reinterpret_cast<HWND>(static_cast<intptr_t>(wcstoull(key.c_str() + 8, nullptr, 16))));
        return {};
    }

    /// size: the pixel size in the low 16 bits; bit 16 set for a light taskbar.
    IconPixels LoadShellIcon(std::wstring const& key, int size)
    {
        IconPixels result{ key, size };
        auto px = size & 0xffff;
        auto light = (size & 0x10000) != 0;
        // A Store app: its own artwork for this theme, as Explorer picks it (the shell's icon is the light-theme one).
        if (auto logo = PackageLogo(AppIdOfKey(key), px, light); !logo.empty())
            if (auto pixels = DecodeImage(logo, px); !pixels.empty())
            {
                result.width = result.height = px;
                result.bgra = std::move(pixels);
                return result;
            }
        size = px;
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

    /// Loads shell icons one at a time on a single background thread, which sleeps while there's nothing to load
    /// (rather than a thread per icon, which signing in with many windows open would start dozens of at once).
    void LoadIconLater(HWND control, std::wstring key, int size)
    {
        static std::mutex lock;
        static std::condition_variable wake;
        static std::deque<std::tuple<HWND, std::wstring, int>> queue;
        static bool started = false;
        {
            std::lock_guard guard(lock);
            queue.emplace_back(control, std::move(key), size);
            if (!started)
            {
                started = true;
                std::thread([]
                {
                    CoInitializeEx(nullptr, COINIT_APARTMENTTHREADED | COINIT_DISABLE_OLE1DDE);
                    for (;;)
                    {
                        std::tuple<HWND, std::wstring, int> job;
                        {
                            std::unique_lock wait(lock);
                            wake.wait(wait, [] { return !queue.empty(); });
                            job = std::move(queue.front());
                            queue.pop_front();
                        }
                        auto& [window, name, pixels] = job;
                        auto result = std::make_unique<IconPixels>();
                        try { *result = LoadShellIcon(name, pixels); }
                        catch (...) { result->key = name; result->size = pixels; }
                        if (PostMessageW(window, IconReady, 0, reinterpret_cast<LPARAM>(result.get()))) result.release();
                    }
                }).detach();
            }
        }
        wake.notify_one();
    }

    DependencyObject TopOf(DependencyObject node)
    {
        while (auto parent = Media::VisualTreeHelper::GetParent(node)) node = parent;
        return node;
    }

    enum class Part { Start, Edge, Indicator, AppButton, Clock };

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
                StartWatching();
                return S_OK;
            }
            catch (...) { return to_hresult(); }
        }

        /// Has XAML tell this DLL about every element Explorer adds, for as long as Explorer runs. It's never stopped:
        /// once a copy stops, XAML refuses to start any again in that Explorer (E_UNEXPECTED), so a copy with nothing to
        /// do (every option off, or a newer one in charge) ignores the reports instead, at a string comparison each.
        void StartWatching()
        {
            if (!m_diagnostics) return;
            // Advising from the UI thread hangs: XAML replays the existing tree on it, and it's waiting for this call.
            // So it's done from another thread; the callbacks still arrive on the UI thread. Retried a few times, as
            // XAML can refuse while Explorer is busy (loading, say).
            std::thread([self = get_strong()]
            {
                for (auto attempt = 1; attempt <= 5; ++attempt)
                {
                    try
                    {
                        auto hr = self->m_diagnostics.as<IVisualTreeService3>()->AdviseVisualTreeChange(self.get());
                        if (SUCCEEDED(hr)) return;
                        Note(std::format(L"watching the taskbar failed (attempt {}): 0x{:08X}", attempt, (unsigned)hr));
                    }
                    catch (hresult_error const& e) { Note(std::format(L"watching the taskbar failed (attempt {}): 0x{:08X}", attempt, (unsigned)e.code().value)); }
                    catch (...) { Note(L"watching the taskbar failed"); }
                    Sleep(1000);
                }
            }).detach();
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
                if (mutation != Add || !element.Type || m_retired) return S_OK;
                std::wstring_view type(element.Type);
                // Cheap filter first: the Start button is one of the taskbar's toggle buttons; the frame is a taskbar's
                // root; a tray icon (new, or rebuilt when its state changes) is an IconView; each app button has a
                // RunningIndicator.
                auto isFrame = type == L"Taskbar.TaskbarFrame";
                auto isTrayIcon = IsIconViewType(type);
                auto isIndicator = element.Name && std::wstring_view(element.Name) == L"RunningIndicator";
                auto isLabel = element.Name && std::wstring_view(element.Name) == L"LabelControl"; // an app button's title
                auto isAppIcon = type == L"Windows.UI.Xaml.Controls.Image" && element.Name && std::wstring_view(element.Name) == L"Icon";
                auto isAppButton = type == L"Taskbar.TaskListButton"; // hidden while the app name stands in for them
                if (!isFrame && !isTrayIcon && !isIndicator && !isLabel && !isAppIcon && !isAppButton && type.find(L"ToggleButton") == std::wstring_view::npos) return S_OK;
                if (++m_added % 64 == 0) Prune();
                EnsureControlWindow();
                Windows::Foundation::IInspectable object;
                if (FAILED(m_diagnostics->GetIInspectableFromHandle(element.Handle, reinterpret_cast<::IInspectable**>(put_abi(object))))) return S_OK;
                auto found = object.try_as<FrameworkElement>();
                if (!found) return S_OK;
                if (isFrame) AddFrame(found);
                else if (isTrayIcon) AddTrayIcon(found);
                else if (isIndicator) AddIndicator(found);
                else if (isLabel) { if (auto text = found.try_as<Controls::TextBlock>()) TrackText(text); }
                else if (isAppButton) AddAppButton(found);
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
            UpdateLabels();
            UpdateDesktopBars(false);
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
            UpdateClocks(); // the clock in the middle is sized as the tray's text is
        }

        /// The app name in place of the app buttons (TrackSwipe's dock is on): the name of the app in front, as a
        /// Mac's menu bar shows it, kept current from a foreground hook. A click on it shows that app's menu from the
        /// dock; the buttons themselves are collapsed, and given back when this goes off.
        void SetAppName(bool on)
        {
            if (on == m_appName) return;
            m_appName = on;
            if (on)
            {
                s_self = this;
                if (!m_foregroundHook)
                    m_foregroundHook = SetWinEventHook(EVENT_SYSTEM_FOREGROUND, EVENT_SYSTEM_FOREGROUND, nullptr, ForegroundChanged, 0, 0, WINEVENT_OUTOFCONTEXT);
                OnForeground(GetForegroundWindow());
                // Nothing usable in front (the taskbar itself, just after Explorer started): start from the topmost
                // app window instead, so the name is never blank.
                if (m_frontName.empty())
                {
                    EnumWindows([](HWND window, LPARAM self) -> BOOL
                    {
                        auto tap = reinterpret_cast<Tap*>(self);
                        int cloaked = 0;
                        if (!IsWindowVisible(window) || IsIconic(window) || GetWindow(window, GW_OWNER) || !GetWindowTextLengthW(window) ||
                            (GetWindowLongW(window, GWL_EXSTYLE) & WS_EX_TOOLWINDOW) ||
                            (SUCCEEDED(DwmGetWindowAttribute(window, DWMWA_CLOAKED, &cloaked, sizeof(cloaked))) && cloaked))
                            return TRUE;
                        tap->OnForeground(window);
                        return tap->m_frontName.empty();
                    }, reinterpret_cast<LPARAM>(this));
                }
                UpdateLabels();
            }
            else
            {
                if (m_foregroundHook) UnhookWinEvent(m_foregroundHook);
                m_foregroundHook = nullptr;
                RemoveLabels();
                WatchFront(nullptr);
                m_front = nullptr;
                m_frontName.clear();
            }
        }

        /// Puts the app name on every taskbar (adding the label to one that hasn't got it yet) and in the right place.
        void UpdateLabels()
        {
            if (!m_appName) return;
            std::erase_if(m_labels, [](auto& label) { return !label.first.get(); });
            for (auto& weak : m_frames)
            {
                auto frame = weak.get();
                if (!frame) continue;
                auto root = FindByName(frame, L"RootGrid").try_as<Controls::Grid>();
                if (!root) continue;
                Controls::TextBlock label{ nullptr };
                for (auto& [owner, existing] : m_labels) if (owner.get() == root) label = existing;
                if (!label) label = AddLabel(root);
                if (!label) continue;
                label.Text(m_frontName);
                label.FontSize(m_textSize >= 1 ? m_textSize : 14);
                label.Foreground(Media::SolidColorBrush(TextColour(frame)));
                label.Margin({ ButtonsLeft(frame, root), 0, 0, 0 });
            }
        }

        /// The taskbar's text colour: white on a dark taskbar, near-black on a light one.
        static Windows::UI::Color TextColour(FrameworkElement const& frame)
        {
            return frame.ActualTheme() == ElementTheme::Dark
                ? Windows::UI::ColorHelper::FromArgb(0xFF, 0xFF, 0xFF, 0xFF) : Windows::UI::ColorHelper::FromArgb(0xE4, 0, 0, 0);
        }

        /// Where the app buttons begin on a taskbar: after the Start button's space (or the padding left while it's hidden).
        double ButtonsLeft(FrameworkElement const& frame, Controls::Grid const& root)
        {
            double left = 12;
            if (auto repeater = FindByName(frame, L"TaskbarFrameRepeater"))
            {
                try { left = repeater.TransformToVisual(root).TransformPoint({ 0, 0 }).X + repeater.Margin().Left; }
                catch (...) {}
                for (auto& item : m_buttons)
                    if (auto start = item.element.get(); start && start.Visibility() == Visibility::Visible && TopOf(start) == TopOf(frame))
                        left += start.ActualWidth();
                left += m_padding > 0 ? 0 : 8;
            }
            return left;
        }

        // ---- What stands in for the app buttons, with the dock on -------------------------------------------

        /// 0: the buttons themselves; 1: the app in front by name; 2: the virtual desktops. The dock being on also
        /// sends minimizing windows into its icons, whichever is shown here.
        void SetTopLeft(int mode, bool dockOn)
        {
            m_dockOn = dockOn;
            bool hide = dockOn && mode != 0;
            if (hide != m_buttonsHidden)
            {
                m_buttonsHidden = hide;
                for (auto& frame : m_frames)
                    if (auto f = frame.get()) FindAppButtons(TopOf(f));
                std::erase_if(m_appButtons, [](auto& item) { return !item.element.get(); });
                for (auto& item : m_appButtons) Apply(item, m_buttonsHidden);
            }
            SetAppName(dockOn && mode == 1);
            SetDesktops(dockOn && mode == 2);
        }

        // ---- Transparency -------------------------------------------------------------------------------
        // The taskbar's glass is one rectangle (BackgroundFill, beside the edge line): drawn less opaque, the desktop
        // shows through it. A property set once, so it costs nothing after.

        void SetTransparency(int percent)
        {
            m_transparency = std::clamp(percent, 0, 100);
            for (auto& frame : m_frames) ApplyTransparency(frame);
        }

        void ApplyTransparency(weak_ref<FrameworkElement> const& weak)
        {
            auto frame = weak.get();
            if (!frame) return;
            auto background = FindByName(frame, L"BackgroundControl");
            auto fill = background ? FindByName(background, L"BackgroundFill") : nullptr;
            if (!fill) return;
            if (m_transparency == 0) fill.ClearValue(UIElement::OpacityProperty());
            else fill.Opacity(1.0 - m_transparency / 100.0);
        }

        // ---- Desktops -----------------------------------------------------------------------------------
        // GNOME's top bar: a dot for each virtual desktop, the current one drawn as a longer pill that slides along as
        // the desktop changes; a click opens the overview (TrackSwipe's Mission Control). Explorer keeps the desktop
        // list and the current one in the registry, which signals a change, so nothing here runs between switches.

        static constexpr double DotSize = 7, PillWidth = 22, DotGap = 6, DimmedDot = 0.45;
        static constexpr int SlideMs = 250;

        void SetDesktops(bool on)
        {
            if (on == m_desktops) return;
            m_desktops = on;
            if (on)
            {
                if (!m_desktopKey)
                    RegOpenKeyExW(HKEY_CURRENT_USER, LR"(Software\Microsoft\Windows\CurrentVersion\Explorer\VirtualDesktops)", 0, KEY_READ | KEY_NOTIFY, &m_desktopKey);
                if (!m_desktopEvent) m_desktopEvent = CreateEventW(nullptr, FALSE, FALSE, nullptr);
                ReadDesktops();
                WatchDesktops();
                UpdateDesktopBars(false);
            }
            else
            {
                if (m_desktopWait) { UnregisterWaitEx(m_desktopWait, nullptr); m_desktopWait = nullptr; }
                RemoveDesktopBars();
            }
        }

        /// Asks the registry to signal the next change to the desktops' key (a notification is good for one change).
        void WatchDesktops()
        {
            if (!m_desktops || !m_desktopKey || !m_desktopEvent || !m_control) return;
            RegNotifyChangeKeyValue(m_desktopKey, FALSE, REG_NOTIFY_CHANGE_LAST_SET, m_desktopEvent, TRUE);
            if (!m_desktopWait)
                RegisterWaitForSingleObject(&m_desktopWait, m_desktopEvent,
                    [](PVOID control, BOOLEAN) { PostMessageW(static_cast<HWND>(control), DesktopsChanged, 0, 0); },
                    m_control, INFINITE, WT_EXECUTEDEFAULT);
        }

        /// On the taskbar's thread, after the registry said the desktops changed.
        void DesktopsMoved()
        {
            if (!m_desktops) return;
            ReadDesktops();
            WatchDesktops();
            UpdateDesktopBars(true);
        }

        void ReadDesktops()
        {
            int count = 0, index = -1;
            DWORD size = 0;
            if (m_desktopKey && RegQueryValueExW(m_desktopKey, L"VirtualDesktopIDs", nullptr, nullptr, nullptr, &size) == ERROR_SUCCESS && size % sizeof(GUID) == 0 && size > 0)
            {
                std::vector<GUID> ids(size / sizeof(GUID));
                if (RegQueryValueExW(m_desktopKey, L"VirtualDesktopIDs", nullptr, nullptr, reinterpret_cast<BYTE*>(ids.data()), &size) == ERROR_SUCCESS)
                {
                    count = static_cast<int>(ids.size());
                    GUID current{};
                    DWORD currentSize = sizeof(current);
                    if (RegQueryValueExW(m_desktopKey, L"CurrentVirtualDesktop", nullptr, nullptr, reinterpret_cast<BYTE*>(&current), &currentSize) == ERROR_SUCCESS && currentSize == sizeof(current))
                        for (int i = 0; i < count; ++i) if (ids[i] == current) index = i;
                }
            }
            if (count == 0) { count = 1; index = 0; } // one desktop: Windows lists none until there's a second
            m_desktopCount = count;
            m_desktopIndex = index;
        }

        /// Puts the dots on every taskbar (adding them to one that hasn't got them), the current desktop's as the pill.
        void UpdateDesktopBars(bool animate)
        {
            if (!m_desktops) return;
            std::erase_if(m_desktopBars, [](auto& bar) { return !bar.first.get(); });
            for (auto& weak : m_frames)
            {
                auto frame = weak.get();
                if (!frame) continue;
                auto root = FindByName(frame, L"RootGrid").try_as<Controls::Grid>();
                if (!root) continue;
                Controls::Border host{ nullptr };
                for (auto& [owner, existing] : m_desktopBars) if (owner.get() == root) host = existing;
                if (!host) host = AddDesktopBar(root);
                if (!host) continue;
                auto dots = host.Child().as<Controls::StackPanel>();
                auto children = dots.Children();
                while (static_cast<int>(children.Size()) > m_desktopCount) children.RemoveAtEnd();
                while (static_cast<int>(children.Size()) < m_desktopCount)
                {
                    Controls::Border dot;
                    dot.Height(DotSize);
                    dot.Width(DotSize);
                    dot.CornerRadius({ DotSize / 2, DotSize / 2, DotSize / 2, DotSize / 2 });
                    dot.Opacity(DimmedDot);
                    children.Append(dot);
                }
                Media::SolidColorBrush brush(TextColour(frame));
                Media::Animation::Storyboard slide;
                for (int i = 0; i < m_desktopCount; ++i)
                {
                    auto dot = children.GetAt(i).as<Controls::Border>();
                    dot.Background(brush);
                    bool current = i == m_desktopIndex;
                    double width = current ? PillWidth : DotSize, opacity = current ? 1.0 : DimmedDot;
                    if (!animate || dot.Width() == width) { dot.Width(width); dot.Opacity(opacity); continue; }
                    // The pill shrinks back to a dot as the next dot grows into it, both eased: GNOME's slide.
                    Media::Animation::CubicEase ease;
                    ease.EasingMode(Media::Animation::EasingMode::EaseOut);
                    Media::Animation::DoubleAnimation grow;
                    grow.To(width);
                    grow.Duration(Duration{ std::chrono::milliseconds(SlideMs), DurationType::TimeSpan });
                    grow.EasingFunction(ease);
                    grow.EnableDependentAnimation(true); // a width: laid out each frame, for the quarter second it runs
                    Media::Animation::Storyboard::SetTarget(grow, dot);
                    Media::Animation::Storyboard::SetTargetProperty(grow, L"Width");
                    slide.Children().Append(grow);
                    Media::Animation::DoubleAnimation fade;
                    fade.To(opacity);
                    fade.Duration(Duration{ std::chrono::milliseconds(SlideMs), DurationType::TimeSpan });
                    Media::Animation::Storyboard::SetTarget(fade, dot);
                    Media::Animation::Storyboard::SetTargetProperty(fade, L"Opacity");
                    slide.Children().Append(fade);
                }
                if (slide.Children().Size() > 0) slide.Begin();
                host.Margin({ ButtonsLeft(frame, root) - 8, 0, 0, 0 }); // the dots' padding sits where the buttons' did
                Automation::AutomationProperties::SetName(host,
                    std::format(L"Desktop {} of {}. Opens Mission Control", m_desktopIndex + 1, m_desktopCount));
                m_desktopHover = frame.ActualTheme() == ElementTheme::Dark
                    ? Windows::UI::ColorHelper::FromArgb(0x1A, 0xFF, 0xFF, 0xFF) : Windows::UI::ColorHelper::FromArgb(0x10, 0, 0, 0);
            }
        }

        Controls::Border AddDesktopBar(Controls::Grid const& root)
        {
            Controls::StackPanel dots;
            dots.Orientation(Controls::Orientation::Horizontal);
            dots.Spacing(DotGap);
            dots.VerticalAlignment(VerticalAlignment::Center);
            Controls::Border host;
            host.Child(dots);
            host.Padding({ 8, 6, 8, 6 });
            host.CornerRadius({ 8, 8, 8, 8 });
            host.Background(Media::SolidColorBrush(Windows::UI::Colors::Transparent())); // hit-testable all over
            host.VerticalAlignment(VerticalAlignment::Center);
            host.HorizontalAlignment(HorizontalAlignment::Left);
            host.PointerEntered([this, weak = make_weak(host)](auto&&, auto&&) { if (auto h = weak.get()) h.Background(Media::SolidColorBrush(m_desktopHover)); });
            host.PointerExited([weak = make_weak(host)](auto&&, auto&&) { if (auto h = weak.get()) h.Background(Media::SolidColorBrush(Windows::UI::Colors::Transparent())); });
            host.Tapped([](auto&&, Input::TappedRoutedEventArgs const& e) { e.Handled(true); OpenOverview(); });
            root.Children().Append(host);
            m_desktopBars.emplace_back(make_weak(root), host);
            return host;
        }

        void RemoveDesktopBars()
        {
            for (auto& [owner, host] : m_desktopBars)
                if (auto root = owner.get())
                {
                    uint32_t index;
                    if (root.Children().IndexOf(host, index)) root.Children().RemoveAt(index);
                }
            m_desktopBars.clear();
        }

        /// GNOME's Activities: TrackSwipe opens Mission Control (or closes it, if it's open).
        static void OpenOverview()
        {
            if (auto trackSwipe = FindWindowW(L"TrackSwipe.Window", L"TrackSwipe tray"))
            {
                DWORD pid = 0;
                GetWindowThreadProcessId(trackSwipe, &pid);
                AllowSetForegroundWindow(pid);
                static const UINT overview = RegisterWindowMessageW(L"TrackSwipe.Overview");
                PostMessageW(trackSwipe, overview, 0, 0);
            }
        }

        // ---- The clock in the middle --------------------------------------------------------------------
        // GNOME's top bar: the date and time centred on the bar ("Nov 10  7:50 PM"), with Windows' own clock hidden
        // meanwhile. A click opens what a click on Windows' clock does: notifications and the calendar. The text is set
        // when the minute turns, by a timer due then, and whenever Explorer's own clock changes its text (it does on
        // waking from sleep, or when the time is changed); nothing runs in between.

        static constexpr UINT_PTR ClockTimer = 32;

        void SetCentreClock(bool on)
        {
            if (on == m_centreClock) return;
            m_centreClock = on;
            if (on)
            {
                s_self = this;
                if (!m_flyoutHook)
                    m_flyoutHook = SetWinEventHook(EVENT_SYSTEM_FOREGROUND, EVENT_SYSTEM_FOREGROUND, nullptr, FlyoutWatch, 0, 0, WINEVENT_OUTOFCONTEXT);
                UpdateClocks();
            }
            else
            {
                if (m_flyoutHook) UnhookWinEvent(m_flyoutHook);
                m_flyoutHook = nullptr;
                if (m_control) KillTimer(m_control, ClockTimer);
                RemoveClocks();
                std::erase_if(m_clockViews, [](auto& item) { return !item.element.get(); });
                for (auto& item : m_clockViews) Apply(item, false);
            }
        }

        /// Puts the clock in the middle of every taskbar (adding it to one that hasn't got it), and hides Windows' own.
        void UpdateClocks()
        {
            if (!m_centreClock) return;
            std::erase_if(m_centreClocks, [](auto& clock) { return !clock.first.get(); });
            std::erase_if(m_clockViews, [](auto& item) { return !item.element.get(); });
            auto text = ClockText();
            for (auto& weak : m_frames)
            {
                auto frame = weak.get();
                if (!frame) continue;
                auto root = FindByName(frame, L"RootGrid").try_as<Controls::Grid>();
                if (!root) continue;
                auto time = FindByName(frame, L"TimeInnerTextBlock").try_as<Controls::TextBlock>();
                if (time)
                {
                    WatchTime(time);
                    if (auto slot = SlotOf(time)) Track(m_clockViews, slot, Part::Clock);
                }
                Controls::Border host{ nullptr };
                for (auto& [owner, existing] : m_centreClocks) if (owner.get() == root) host = existing;
                if (!host) host = AddClock(root);
                auto label = host.Child().as<Controls::TextBlock>();
                if (label.Text() != text) label.Text(text);
                // The size Windows' clock is drawn at (or TrackSwipe's, with larger taskbar text).
                label.FontSize(m_textSize >= 1 ? m_textSize : time ? time.FontSize() : 12);
                label.Foreground(Media::SolidColorBrush(TextColour(frame)));
                m_clockHover = frame.ActualTheme() == ElementTheme::Dark
                    ? Windows::UI::ColorHelper::FromArgb(0x1A, 0xFF, 0xFF, 0xFF) : Windows::UI::ColorHelper::FromArgb(0x10, 0, 0, 0);
            }
            for (auto& item : m_clockViews) Apply(item, true);
            ArmClockTimer();
        }

        /// Just the text, as the minute turns.
        void ClockTick()
        {
            if (!m_centreClock) return;
            auto text = ClockText();
            for (auto& [owner, host] : m_centreClocks)
                if (auto label = host.Child().try_as<Controls::TextBlock>(); label && label.Text() != text) label.Text(text);
            ArmClockTimer();
        }

        /// A timer due just after the next minute begins.
        void ArmClockTimer()
        {
            if (!m_control) return;
            SYSTEMTIME now;
            GetLocalTime(&now);
            SetTimer(m_control, ClockTimer, (60 - now.wSecond) * 1000 - now.wMilliseconds + 20, nullptr);
        }

        /// Explorer's own clock changing its text (hidden or not) sets ours too: after sleep, or a change of time.
        void WatchTime(Controls::TextBlock const& time)
        {
            std::erase_if(m_timeTexts, [](auto& weak) { return !weak.get(); });
            for (auto& weak : m_timeTexts) if (weak.get() == time) return;
            m_timeTexts.push_back(make_weak(time));
            time.RegisterPropertyChangedCallback(Controls::TextBlock::TextProperty(), [this](DependencyObject const&, DependencyProperty const&)
            {
                try { ClockTick(); }
                catch (...) {}
            });
        }

        /// The date as GNOME shows it, in the order the user's dates take ("Nov 10", or "10 Nov"), then the time as the
        /// taskbar's clock shows it (the short time format: 12 or 24 hours, as set in Windows).
        static std::wstring ClockText()
        {
            SYSTEMTIME now;
            GetLocalTime(&now);
            DWORD order = 0; // LOCALE_IDATE: 0 month first, 1 day first, 2 year first
            GetLocaleInfoEx(LOCALE_NAME_USER_DEFAULT, LOCALE_IDATE | LOCALE_RETURN_NUMBER, reinterpret_cast<LPWSTR>(&order), sizeof(order) / sizeof(wchar_t));
            wchar_t date[64]{}, time[64]{}, format[80]{};
            GetDateFormatEx(LOCALE_NAME_USER_DEFAULT, 0, &now, order == 1 ? L"d MMM" : L"MMM d", date, ARRAYSIZE(date), nullptr);
            GetLocaleInfoEx(LOCALE_NAME_USER_DEFAULT, LOCALE_SSHORTTIME, format, ARRAYSIZE(format));
            GetTimeFormatEx(LOCALE_NAME_USER_DEFAULT, format[0] ? 0 : TIME_NOSECONDS, &now, format[0] ? format : nullptr, time, ARRAYSIZE(time));
            return std::format(L"{} {}", date, time); // an em space between, as GNOME spaces them
        }

        Controls::Border AddClock(Controls::Grid const& root)
        {
            Controls::TextBlock label;
            label.FontWeight(winrt::Windows::UI::Text::FontWeights::SemiBold());
            label.VerticalAlignment(VerticalAlignment::Center);
            label.IsTextScaleFactorEnabled(false); // sized above, from the clock it stands in for
            Controls::Border host;
            host.Child(label);
            host.Padding({ 10, 4, 10, 4 });
            host.CornerRadius({ 8, 8, 8, 8 });
            host.Background(Media::SolidColorBrush(Windows::UI::Colors::Transparent())); // hit-testable all over
            host.VerticalAlignment(VerticalAlignment::Center);
            host.HorizontalAlignment(HorizontalAlignment::Center);
            // The middle of the whole bar, whatever columns or rows it's laid out in.
            if (auto columns = static_cast<int>(root.ColumnDefinitions().Size()); columns > 1) Controls::Grid::SetColumnSpan(host, columns);
            if (auto rows = static_cast<int>(root.RowDefinitions().Size()); rows > 1) Controls::Grid::SetRowSpan(host, rows);
            host.PointerEntered([this, weak = make_weak(host)](auto&&, auto&&) { if (auto h = weak.get()) h.Background(Media::SolidColorBrush(m_clockHover)); });
            host.PointerExited([weak = make_weak(host)](auto&&, auto&&) { if (auto h = weak.get()) h.Background(Media::SolidColorBrush(Windows::UI::Colors::Transparent())); });
            host.Tapped([this, weak = make_weak(root)](auto&&, Input::TappedRoutedEventArgs const& e)
            {
                e.Handled(true);
                if (auto r = weak.get()) OpenCalendar(r);
            });
            Automation::AutomationProperties::SetName(host, L"Clock. Opens notifications and the calendar");
            root.Children().Append(host);
            m_centreClocks.emplace_back(make_weak(root), host);
            return host;
        }

        void RemoveClocks()
        {
            for (auto& [owner, host] : m_centreClocks)
                if (auto root = owner.get())
                {
                    uint32_t index;
                    if (root.Children().IndexOf(host, index)) root.Children().RemoveAt(index);
                }
            m_centreClocks.clear();
        }

        /// What a click on Windows' clock opens: its own button is asked to do it (it's still there, beside the hidden
        /// clock), or failing that, Windows is asked for notifications. Not when the click is the one that just closed
        /// them (they go when anything else is clicked): a second click on Windows' clock closes them too.
        void OpenCalendar(Controls::Grid const& root)
        {
            if (m_flyoutInFront || GetTickCount64() - m_flyoutLeftAt < 500) return;
            if (auto button = FindByName(root, L"NotificationCenterButton"))
                try
                {
                    auto peer = Automation::Peers::FrameworkElementAutomationPeer::CreatePeerForElement(button);
                    if (auto invoke = peer ? peer.GetPattern(Automation::Peers::PatternInterface::Invoke) : nullptr)
                    {
                        invoke.as<Automation::Provider::IInvokeProvider>().Invoke();
                        return;
                    }
                }
                catch (...) {}
            ShellExecuteW(nullptr, L"open", L"ms-actioncenter:", nullptr, nullptr, SW_SHOWNORMAL);
        }

        /// Whether a window is one of the shell's panels (notifications and the calendar, quick settings).
        static bool IsShellPanel(HWND window)
        {
            DWORD pid = 0;
            if (!window || !GetWindowThreadProcessId(window, &pid) || !pid) return false;
            HANDLE process = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, FALSE, pid);
            if (!process) return false;
            wchar_t path[MAX_PATH]{};
            DWORD size = ARRAYSIZE(path);
            auto named = QueryFullProcessImageNameW(process, 0, path, &size);
            CloseHandle(process);
            if (!named) return false;
            std::wstring_view file(path, size);
            file = file.substr(file.find_last_of(L'\\') + 1);
            return _wcsicmp(std::wstring(file).c_str(), L"ShellExperienceHost.exe") == 0 || _wcsicmp(std::wstring(file).c_str(), L"ShellHost.exe") == 0;
        }

        static void CALLBACK FlyoutWatch(HWINEVENTHOOK, DWORD, HWND window, LONG, LONG, DWORD, DWORD)
        {
            try
            {
                if (!s_self) return;
                bool panel = IsShellPanel(window);
                if (s_self->m_flyoutInFront && !panel) s_self->m_flyoutLeftAt = GetTickCount64();
                s_self->m_flyoutInFront = panel;
            }
            catch (...) {}
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
                out += std::format(L"=== taskbar (theme {}, requested {})\n", frame.ActualTheme() == ElementTheme::Dark ? L"dark" : L"light", static_cast<int>(frame.RequestedTheme()));
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
            return part == Part::Edge ? m_edgeHidden : part == Part::Indicator ? m_indicatorsHidden : part == Part::AppButton ? m_buttonsHidden : part == Part::Clock ? m_centreClock : m_startHidden;
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
            if (!FirstHook(element)) return;
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
                try { FindEdge(weak); for (auto& item : m_edges) Apply(item, m_edgeHidden); Pad(weak); ApplyTransparency(weak); UpdateDesktopBars(false); UpdateClocks(); }
                catch (...) {}
            });
            FindEdge(m_frames.back());
            for (auto& item : m_edges) Apply(item, m_edgeHidden);
            Pad(m_frames.back());
            ApplyTransparency(m_frames.back());
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
            if (!FirstHook(icon)) return;
            icon.Loaded([this, weak = make_weak(icon)](auto&&, auto&&)
            {
                try
                {
                    if (auto i = weak.get(); i && (m_iconSize > 0 || m_textSize > 0)) ScaleTray(i);
                    UpdateClocks(); // a rebuilt clock is hidden again
                }
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
            m_iconCache.insert_or_assign(key, CachedIcon{ bitmap, ++m_iconClock }); // kept even when empty, so a missing icon isn't looked up again
            for (auto& item : m_appIcons)
            {
                if (auto image = item.image.get())
                    if (auto current = ButtonKeyOf(image); !current.empty()) item.key = current;
                if (item.key == pixels.key) ApplyAppIcon(item);
            }
            TrimIconCache();
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

        /// Whether element hasn't had a Loaded handler from this DLL yet (watching again replays the whole tree).
        bool FirstHook(FrameworkElement const& element)
        {
            for (auto& weak : m_hooked) if (weak.get() == element) return false;
            m_hooked.push_back(make_weak(element));
            return true;
        }

        /// Forgets elements Explorer has discarded.
        void Prune()
        {
            auto dead = [](auto const& item) { return !item.element.get(); };
            std::erase_if(m_buttons, dead);
            std::erase_if(m_edges, dead);
            std::erase_if(m_indicators, dead);
            std::erase_if(m_appButtons, dead);
            std::erase_if(m_glyphs, dead);
            std::erase_if(m_slots, dead);
            std::erase_if(m_texts, dead);
            std::erase_if(m_clockSlots, dead);
            std::erase_if(m_appIcons, [](auto const& item) { return !item.image.get(); });
            std::erase_if(m_frames, [](auto const& weak) { return !weak.get(); });
            std::erase_if(m_hooked, [](auto const& weak) { return !weak.get(); });
            TrimIconCache();
        }

        /// Most icons kept. Apps on other desktops keep theirs (so switching back shows them at once rather than
        /// reloading), up to this many; past it, the least recently shown that no button has now go first.
        static constexpr size_t MaxCachedIcons = 64;

        /// Bounds the cache, so it doesn't grow with every window ever opened.
        void TrimIconCache()
        {
            if (m_iconCache.size() <= MaxCachedIcons) return;
            std::set<std::wstring> live;
            for (auto& item : m_appIcons) if (item.image.get()) live.insert(item.key);
            std::vector<std::pair<unsigned long long, std::pair<std::wstring, int>>> idle;
            for (auto const& [key, cached] : m_iconCache) if (!live.contains(key.first)) idle.emplace_back(cached.used, key);
            std::sort(idle.begin(), idle.end());
            for (size_t i = 0; i < idle.size() && m_iconCache.size() > MaxCachedIcons; ++i) m_iconCache.erase(idle[i].second);
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
            if (FirstHook(image))
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
                            // A new bitmap from Explorer (a reused button, or a redraw): show the shell's icon for
                            // whichever app the button holds now. It's cached, so this fetches nothing again.
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
                auto key = std::make_pair(item.key, PixelsOf(image) | (m_lightTaskbar ? 0x10000 : 0));
                auto cached = m_iconCache.find(key);
                Media::ImageSource wanted = cached != m_iconCache.end() ? cached->second.bitmap : nullptr;
                if (cached != m_iconCache.end()) cached->second.used = ++m_iconClock;
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
                    LoadIconLater(m_control, key.first, key.second);

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
                    if (auto brush = text.Foreground().try_as<Media::SolidColorBrush>()) { auto colour = brush.Color(); line += std::format(L" colour #{:02X}{:02X}{:02X}{:02X}", colour.A, colour.R, colour.G, colour.B); }
                    line += element.ActualTheme() == ElementTheme::Dark ? L" dark" : L" light";
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
                            if (cached.bitmap && cached.bitmap == source) { line += std::format(L" for [{}]", key.first); break; }
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

        // ---- App name ---------------------------------------------------------------------------------------

        void AddAppButton(FrameworkElement const& element)
        {
            Track(m_appButtons, element, Part::AppButton);
            for (auto& item : m_appButtons) if (item.element.get() == element) Apply(item, m_buttonsHidden);
        }

        void FindAppButtons(DependencyObject const& node, int depth = 0)
        {
            if (depth > 40) return;
            if (get_class_name(node) == L"Taskbar.TaskListButton")
            {
                if (auto element = node.try_as<FrameworkElement>()) Track(m_appButtons, element, Part::AppButton);
                return;
            }
            auto count = Media::VisualTreeHelper::GetChildrenCount(node);
            for (int i = 0; i < count; ++i) FindAppButtons(Media::VisualTreeHelper::GetChild(node, i), depth + 1);
        }

        Controls::TextBlock AddLabel(Controls::Grid const& root)
        {
            Controls::TextBlock label;
            label.FontWeight(winrt::Windows::UI::Text::FontWeights::SemiBold());
            label.VerticalAlignment(VerticalAlignment::Center);
            label.HorizontalAlignment(HorizontalAlignment::Left);
            label.TextTrimming(TextTrimming::CharacterEllipsis);
            label.MaxWidth(360);
            label.IsHitTestVisible(true);
            Automation::AutomationProperties::SetName(label, L"App in front");
            // Its menu either way: a Mac's app name opens a menu on a click, and right-click is the taskbar's habit.
            label.Tapped([this](auto&&, Input::TappedRoutedEventArgs const& e) { e.Handled(true); ShowAppMenu(); });
            label.RightTapped([this](auto&&, Input::RightTappedRoutedEventArgs const& e) { e.Handled(true); ShowAppMenu(); });
            root.Children().Append(label);
            m_labels.emplace_back(make_weak(root), label);
            return label;
        }

        void RemoveLabels()
        {
            for (auto& [owner, label] : m_labels)
                if (auto root = owner.get())
                {
                    uint32_t index;
                    if (root.Children().IndexOf(label, index)) root.Children().RemoveAt(index);
                }
            m_labels.clear();
        }

        static inline Tap* s_self = nullptr;

        static void CALLBACK ForegroundChanged(HWINEVENTHOOK, DWORD, HWND window, LONG, LONG, DWORD, DWORD)
        {
            try { if (s_self) s_self->OnForeground(window); }
            catch (...) {}
        }

        /// A new app in front: its name on the taskbar. The taskbar itself (clicking the name puts it in front), the
        /// shell's own surfaces and tool windows (the dock, its menus) leave the name as it was.
        void OnForeground(HWND window)
        {
            if (!window || !m_appName) return;
            wchar_t cls[128] = {};
            GetClassNameW(window, cls, ARRAYSIZE(cls));
            std::wstring_view name(cls);
            if (name == L"Shell_TrayWnd" || name == L"Shell_SecondaryTrayWnd" || name == L"Windows.UI.Core.CoreWindow" ||
                name.starts_with(L"XamlExplorerHostIslandWindow") || name == L"TopLevelWindowForOverflowXamlIsland" ||
                name == L"NotifyIconOverflowWindow" || name == L"ForegroundStaging" || name == L"MultitaskingViewFrame")
                return;
            // The desktop in front: no app is, so no name (as when the last window closes).
            if (name == L"Progman" || name == L"WorkerW") { SetFront(nullptr, L""); return; }
            if (GetWindowLongW(window, GWL_EXSTYLE) & WS_EX_TOOLWINDOW) return;
            auto owner = GetAncestor(window, GA_ROOTOWNER);
            if (!owner) owner = window;
            SetFront(owner, AppNameOf(owner));
        }

        void SetFront(HWND window, std::wstring const& text)
        {
            m_front = window;
            WatchFront(window);
            if (text == m_frontName) return;
            m_frontName = text;
            UpdateLabels();
        }

        /// The app in front closing, hiding or minimizing its window often leaves nothing in front, which raises no
        /// foreground event: so that one app's windows are watched for those, and the name looked at again after.
        /// Only its process's events are delivered, so this costs nothing elsewhere.
        void WatchFront(HWND window)
        {
            DWORD pid = 0;
            if (window) GetWindowThreadProcessId(window, &pid);
            if (pid == m_frontProcess) return;
            for (auto& hook : m_frontHooks) if (hook) UnhookWinEvent(hook);
            m_frontHooks = {};
            m_frontProcess = pid;
            if (!pid) return;
            m_frontHooks[0] = SetWinEventHook(EVENT_OBJECT_DESTROY, EVENT_OBJECT_HIDE, nullptr, FrontEvent, pid, 0, WINEVENT_OUTOFCONTEXT);
            m_frontHooks[1] = SetWinEventHook(EVENT_SYSTEM_MINIMIZESTART, EVENT_SYSTEM_MINIMIZESTART, nullptr, FrontEvent, pid, 0, WINEVENT_OUTOFCONTEXT);
        }

        static void CALLBACK FrontEvent(HWINEVENTHOOK, DWORD event, HWND window, LONG object, LONG, DWORD, DWORD)
        {
            try
            {
                auto tap = s_self;
                if (!tap || object != OBJID_WINDOW || window != tap->m_front || event == EVENT_OBJECT_SHOW) return;
                // A moment later, once Windows has settled what's in front now.
                SetTimer(tap->m_control, RecheckTimer, 150, nullptr);
            }
            catch (...) {}
        }

        /// After the app in front closed, hid or minimized its window: the name of whatever app is in front now, or
        /// none if nothing (or only the desktop) is.
        void Recheck()
        {
            KillTimer(m_control, RecheckTimer);
            auto front = GetForegroundWindow();
            if (front && front != m_front && IsWindowVisible(front) && !IsIconic(front)) OnForeground(front);
            if (m_front && (!IsWindow(m_front) || !IsWindowVisible(m_front) || IsIconic(m_front))) SetFront(nullptr, L"");
        }

        static constexpr UINT_PTR RecheckTimer = 31;

        // ---- Minimize targets: the dock's icons --------------------------------------------------------------
        //
        // When a window minimizes or comes back, Windows asks the shell where its taskbar button is (the shell hook's
        // HSHELL_GETMINRECT) and animates it to and from there. With the dock on, those buttons are hidden, so the
        // answer is the window's icon on the dock instead: the taskbar answers first, then the dock's icon (from the
        // map the dock keeps TrackSwipe up to date with, passed on here) replaces it. Windows' own animation then
        // goes into the dock and out of it, from a dock click, the title bar's button or the keyboard alike.

        std::map<HWND, RECT> m_dockIcons;
        bool m_subclassed = false;
        int m_minRectNotes = 0; // what the minimize-target path does, logged for its first few

        /// The dock's icon map, from TrackSwipe: per window, its handle then the icon's left/top and right/bottom.
        void ReceiveDockIcons(COPYDATASTRUCT const* copy)
        {
            if (!copy || copy->dwData != 0x54534B44 /* "TSKD" */) return;
            auto values = static_cast<long long const*>(copy->lpData);
            auto before = m_dockIcons.size();
            m_dockIcons.clear();
            for (DWORD i = 0; i < copy->cbData / 24; ++i)
            {
                auto window = reinterpret_cast<HWND>(static_cast<intptr_t>(values[i * 3]));
                long long topLeft = values[i * 3 + 1], bottomRight = values[i * 3 + 2];
                RECT icon{ static_cast<LONG>(topLeft & 0xFFFFFFFF), static_cast<LONG>(topLeft >> 32),
                           static_cast<LONG>(bottomRight & 0xFFFFFFFF), static_cast<LONG>(bottomRight >> 32) };
                if (icon.right > icon.left && icon.bottom > icon.top) m_dockIcons[window] = icon;
            }
            if (m_dockIcons.size() != before && m_minRectNotes < 12)
            {
                ++m_minRectNotes;
                auto first = m_dockIcons.empty() ? RECT{} : m_dockIcons.begin()->second;
                Note(std::format(L"dock icons: {} windows (first at {},{},{},{})", m_dockIcons.size(), first.left, first.top, first.right, first.bottom));
            }
            SubclassShellHookWindows();
        }

        /// Explorer answers the shell hook on its taskbar windows (which one varies by build): each of them, on this
        /// thread, gets a look at the answer.
        void SubclassShellHookWindows()
        {
            if (m_subclassed) return;
            m_subclassed = true;
            s_self = this;
            auto tray = FindWindowW(L"Shell_TrayWnd", nullptr);
            if (!tray) return;
            auto mine = GetCurrentThreadId();
            auto hook = [&](HWND window)
            {
                if (window && GetWindowThreadProcessId(window, nullptr) == mine)
                    Note(std::format(L"minimize targets: watching Shell_TrayWnd ({})", SetWindowSubclass(window, ShellHookProc, 1, 0) ? L"ok" : L"failed"));
            };
            hook(tray);
            EnumChildWindows(tray, [](HWND child, LPARAM mineParam) -> BOOL
            {
                wchar_t cls[64] = {};
                GetClassNameW(child, cls, ARRAYSIZE(cls));
                std::wstring_view name(cls);
                if ((name == L"MSTaskSwWClass" || name == L"MSTaskListWClass") && GetWindowThreadProcessId(child, nullptr) == static_cast<DWORD>(mineParam))
                    Note(std::format(L"minimize targets: watching {} ({})", name, SetWindowSubclass(child, ShellHookProc, 1, 0) ? L"ok" : L"failed"));
                return TRUE;
            }, static_cast<LPARAM>(mine));
            using GetTaskmanWindowFn = HWND(WINAPI*)();
            if (auto taskman = reinterpret_cast<GetTaskmanWindowFn>(GetProcAddress(GetModuleHandleW(L"user32.dll"), "GetTaskmanWindow")))
                Note(std::format(L"minimize targets: taskman window is 0x{:X}", reinterpret_cast<uintptr_t>(taskman())));
            if (GetWindowThreadProcessId(tray, nullptr) != mine)
                Note(L"the taskbar's windows are on another thread: minimize targets stay on the taskbar");
        }

        static LRESULT CALLBACK ShellHookProc(HWND window, UINT message, WPARAM wParam, LPARAM lParam, UINT_PTR, DWORD_PTR)
        {
            static const UINT shellHook = RegisterWindowMessageW(L"SHELLHOOK");
            if (message != shellHook || (wParam & 0x7FFF) != HSHELL_GETMINRECT) return DefSubclassProc(window, message, wParam, lParam);
            auto result = DefSubclassProc(window, message, wParam, lParam);
            try
            {
                auto tap = s_self;
                auto info = reinterpret_cast<SHELLHOOKINFO*>(lParam);
                if (!tap || !info) return result;
                auto said = info->rc;
                auto saidShort = reinterpret_cast<SHORT const*>(&said);
                auto found = tap->m_dockIcons.find(info->hwnd);
                if (found == tap->m_dockIcons.end()) found = tap->m_dockIcons.find(GetAncestor(info->hwnd, GA_ROOTOWNER));
                bool answer = tap->m_dockOn && found != tap->m_dockIcons.end();
                if (tap->m_minRectNotes < 12)
                {
                    ++tap->m_minRectNotes;
                    wchar_t cls[64] = {};
                    GetClassNameW(window, cls, ARRAYSIZE(cls));
                    Note(std::format(L"GETMINRECT for 0x{:X} at {}: taskbar said {},{},{},{} as 16-bit (returned {}); dock {}, {} dock icons, {}",
                        reinterpret_cast<uintptr_t>(info->hwnd), cls, saidShort[0], saidShort[1], saidShort[2], saidShort[3], static_cast<long long>(result),
                        tap->m_dockOn ? L"on" : L"off", tap->m_dockIcons.size(),
                        answer ? std::format(L"answering {},{},{},{}", found->second.left, found->second.top, found->second.right, found->second.bottom) : std::wstring(L"not answering")));
                }
                if (!answer) return result;
                // Windows reads the answer as four 16-bit numbers (left, top, right, bottom) in the RECT's place, not as
                // a RECT — the taskbar's own, read that way, is its button's place (108,0,109,48 on a taskbar at the
                // top) — and a RECT written here reads as an empty, inverted box: the window just vanished, and came
                // back from nowhere. Measured on Windows 11: written as 16-bit, it shrinks into the dock icon and grows
                // back out of it.
                auto const& icon = found->second;
                auto out = reinterpret_cast<SHORT*>(&info->rc);
                out[0] = static_cast<SHORT>(icon.left);
                out[1] = static_cast<SHORT>(icon.top);
                out[2] = static_cast<SHORT>(icon.right);
                out[3] = static_cast<SHORT>(icon.bottom);
                return TRUE;
            }
            catch (...) { return result; }
        }

        /// What the app is called: its name in the Apps folder (Store apps, and desktop apps with an ID of their own),
        /// else its program's file description, else the program's file name. Remembered per app.
        std::wstring AppNameOf(HWND window)
        {
            auto id = AppIdOf(window);
            DWORD pid = 0;
            GetWindowThreadProcessId(window, &pid);
            std::wstring path;
            if (auto process = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, FALSE, pid))
            {
                wchar_t buffer[1024];
                DWORD length = ARRAYSIZE(buffer);
                if (QueryFullProcessImageNameW(process, 0, buffer, &length)) path.assign(buffer, length);
                if (id.empty())
                {
                    UINT32 idLength = 0;
                    if (GetApplicationUserModelId(process, &idLength, nullptr) == ERROR_INSUFFICIENT_BUFFER && idLength > 0)
                    {
                        std::wstring packaged(idLength, L'\0');
                        if (GetApplicationUserModelId(process, &idLength, packaged.data()) == ERROR_SUCCESS) id = packaged.c_str();
                    }
                }
                CloseHandle(process);
            }
            auto key = id.empty() ? path : id;
            if (auto known = m_names.find(key); known != m_names.end()) return known->second;

            std::wstring result;
            if (!id.empty())
            {
                com_ptr<IShellItem> item;
                PWSTR display = nullptr;
                if (SUCCEEDED(SHCreateItemInKnownFolder(AppsFolder, 0, id.c_str(), IID_PPV_ARGS(item.put()))) &&
                    SUCCEEDED(item->GetDisplayName(SIGDN_NORMALDISPLAY, &display)) && display)
                {
                    result = display;
                    CoTaskMemFree(display);
                }
            }
            if (result.empty() && !path.empty())
            {
                auto file = path.substr(path.find_last_of(L'\\') + 1);
                if (_wcsicmp(file.c_str(), L"explorer.exe") == 0) result = L"File Explorer";
                else result = FileDescription(path);
                if (result.empty()) result = file.substr(0, file.find_last_of(L'.'));
            }
            m_names[key] = result;
            return result;
        }

        static std::wstring FileDescription(std::wstring const& path)
        {
            DWORD ignored = 0, size = GetFileVersionInfoSizeW(path.c_str(), &ignored);
            if (!size) return {};
            std::vector<uint8_t> data(size);
            if (!GetFileVersionInfoW(path.c_str(), 0, size, data.data())) return {};
            struct Translation { WORD language, codepage; } *translation = nullptr;
            UINT length = 0;
            if (!VerQueryValueW(data.data(), L"\\VarFileInfo\\Translation", reinterpret_cast<void**>(&translation), &length) || length < sizeof(Translation)) return {};
            auto query = std::format(L"\\StringFileInfo\\{:04x}{:04x}\\FileDescription", translation->language, translation->codepage);
            wchar_t* text = nullptr;
            if (!VerQueryValueW(data.data(), query.c_str(), reinterpret_cast<void**>(&text), &length) || !text || !length) return {};
            std::wstring description(text);
            while (!description.empty() && iswspace(description.back())) description.pop_back();
            return description;
        }

        /// Asks TrackSwipe's dock to show the front app's menu (jump list, pin, close) at the cursor. The dock has the
        /// menu; this only says which app and where. Lets the dock take the foreground, which its menu needs.
        void ShowAppMenu()
        {
            POINT at{};
            GetCursorPos(&at);
            HWND dock = nullptr;
            EnumWindows([](HWND window, LPARAM found) -> BOOL
            {
                wchar_t cls[64] = {};
                GetClassNameW(window, cls, ARRAYSIZE(cls));
                if (std::wstring_view(cls).starts_with(L"Anchor.MessageWindow.")) { *reinterpret_cast<HWND*>(found) = window; return FALSE; }
                return TRUE;
            }, reinterpret_cast<LPARAM>(&dock));
            if (!dock || !m_front) return;
            DWORD pid = 0;
            GetWindowThreadProcessId(dock, &pid);
            AllowSetForegroundWindow(pid);
            static const UINT appMenu = RegisterWindowMessageW(L"TrackSwipe.Dock.AppMenu");
            PostMessageW(dock, appMenu, reinterpret_cast<WPARAM>(m_front), MAKELPARAM(static_cast<short>(at.x), static_cast<short>(at.y)));
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
                m_uiSettings.ColorValuesChanged([control = m_control](auto&&, auto&&) { PostMessageW(control, ColorsChanged, 0, 0); });
            }
            catch (...) {}
            // TrackSwipe may run as administrator; let its commands through.
            for (auto message : { TrackSwipeTap::StartButton, TrackSwipeTap::EdgeLine, TrackSwipeTap::Query, TrackSwipeTap::Dump, TrackSwipeTap::TrayIcons, TrackSwipeTap::Indicators, TrackSwipeTap::TrayText, TrackSwipeTap::AppIcons, TrackSwipeTap::Retire, TrackSwipeTap::AppName, TrackSwipeTap::Transparency, TrackSwipeTap::CentreClock, static_cast<UINT>(WM_COPYDATA) })
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
                    case TrackSwipeTap::Retire: tap->m_retired = true; return 1;
                    case TrackSwipeTap::AppName: tap->SetTopLeft(static_cast<int>(wParam), lParam != 0); return 1;
                    case TrackSwipeTap::Transparency: tap->SetTransparency(static_cast<int>(wParam)); return 1;
                    case TrackSwipeTap::CentreClock: tap->SetCentreClock(wParam != 0); return 1;
                    case DesktopsChanged: tap->DesktopsMoved(); return 1;
                    case WM_COPYDATA: tap->ReceiveDockIcons(reinterpret_cast<COPYDATASTRUCT const*>(lParam)); return 1;
                    case WM_TIMER:
                        if (wParam == RecheckTimer) { tap->Recheck(); return 0; }
                        if (wParam == ClockTimer) { tap->ClockTick(); return 0; }
                        break;
                    case ColorsChanged:
                        // Light and dark taskbars show different artwork for some apps.
                        if (auto light = LightTaskbar(); light != tap->m_lightTaskbar)
                        {
                            tap->m_lightTaskbar = light;
                            tap->m_iconCache.clear();
                            for (auto& item : tap->m_appIcons) tap->ApplyAppIcon(item);
                        }
                        tap->UpdateLabels(); // the app name's colour follows the taskbar's
                        tap->UpdateDesktopBars(false); // and so do the desktop dots
                        tap->UpdateClocks(); // and the clock's
                        for (auto& frame : tap->m_frames) tap->ApplyTransparency(frame);
                        return 1;
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
        bool m_lightTaskbar = LightTaskbar(); // which artwork Store apps' icons use
        bool m_retired = false;       // a newer TrackSwipe's DLL has taken over: reports are ignored
        unsigned m_added = 0;         // elements tracked, for pruning now and then
        std::vector<weak_ref<FrameworkElement>> m_hooked; // elements given a Loaded handler
        struct CachedIcon
        {
            Media::ImageSource bitmap{ nullptr }; // null: there's no icon to be had
            unsigned long long used = 0;          // when last shown (m_iconClock)
        };
        std::map<std::pair<std::wstring, int>, CachedIcon> m_iconCache; // shell icons by button and pixel size
        unsigned long long m_iconClock = 0;
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
        bool m_appName = false;                // the app name stands in for the app buttons
        bool m_buttonsHidden = false;          // something stands in for the app buttons (the name, or the desktops)
        bool m_dockOn = false;                 // TrackSwipe's dock is on: windows minimize into it
        int m_transparency = 0;                // the taskbar's background, in percent see-through
        bool m_desktops = false;               // the desktops stand in for the app buttons
        HKEY m_desktopKey = nullptr;           // Explorer's list of them, watched for changes
        HANDLE m_desktopEvent = nullptr, m_desktopWait = nullptr;
        int m_desktopCount = 1, m_desktopIndex = 0;
        Windows::UI::Color m_desktopHover{};   // the dots' background under the pointer
        std::vector<std::pair<weak_ref<Controls::Grid>, Controls::Border>> m_desktopBars; // per taskbar
        bool m_centreClock = false;            // the date and time in the middle, Windows' clock hidden
        std::vector<std::pair<weak_ref<Controls::Grid>, Controls::Border>> m_centreClocks; // per taskbar
        std::vector<Collapsible> m_clockViews;  // Windows' clocks, collapsed meanwhile
        std::vector<weak_ref<Controls::TextBlock>> m_timeTexts; // their text, watched for changes
        Windows::UI::Color m_clockHover{};
        HWINEVENTHOOK m_flyoutHook = nullptr;  // which window is in front, to know a click closed the shell's panel
        bool m_flyoutInFront = false;
        ULONGLONG m_flyoutLeftAt = 0;
        std::vector<Collapsible> m_appButtons; // collapsed meanwhile
        std::vector<std::pair<weak_ref<Controls::Grid>, Controls::TextBlock>> m_labels; // per taskbar
        std::map<std::wstring, std::wstring> m_names; // app names, by AppUserModelID or program path
        HWINEVENTHOOK m_foregroundHook = nullptr;
        HWND m_front = nullptr;       // the app window in front
        DWORD m_frontProcess = 0;     // whose windows are watched for closing, hiding, minimizing
        std::array<HWINEVENTHOOK, 2> m_frontHooks{};
        std::wstring m_frontName;     // and what it's called
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
