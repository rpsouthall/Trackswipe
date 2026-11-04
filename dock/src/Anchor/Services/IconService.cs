using System.Net.Http;
using System.Runtime.InteropServices;
using Anchor.Interop;
using Anchor.Models;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Storage.Streams;

namespace Anchor.Services;

/// <summary>
/// Resolves a dock item's visual icon. Apps / files / folders use the Windows shell thumbnail
/// pipeline (the same crisp, theme-correct icons Explorer shows). Web links auto-fetch the
/// site's favicon over HTTP, cached to disk so it only downloads once and works offline after.
/// </summary>
public static class IconService
{
    // Shared client: favicons are tiny, follow redirects, and a UA header avoids servers that
    // reject "no user agent" requests.
    private static readonly HttpClient Http = CreateClient();

    // Alongside dock.json, so an override of Anchor's data directory takes the cache with it
    // rather than leaving downloaded favicons in the real profile (see DockStore.DataDirectory).
    private static readonly string CacheDir = Path.Combine(DockStore.DataDirectory, "IconCache");

    // Existence probes run on the pool. On a UNC path to a host that is not answering,
    // File.Exists / Directory.Exists block for the SMB connect timeout (21 s per host, measured),
    // and LoadIconAsync is started from the UI thread — everything before its first real await runs
    // there, so a dock with two dead shares took 43 s to bring up its tray icon and shortcut.
    // One probe per path at a time: a reload (language, import) of a dock full of dead shares then
    // waits on the probes already in flight instead of parking a pool thread for each again.
    private static readonly Dictionary<string, Task<bool>> Probes = new(StringComparer.OrdinalIgnoreCase);

    private static Task<bool> ProbeAsync(string? path, bool directory)
    {
        if (string.IsNullOrWhiteSpace(path))
            return Task.FromResult(false);

        string key = (directory ? "d:" : "f:") + path;
        lock (Probes)
        {
            if (Probes.TryGetValue(key, out var running))
                return running;

            var probe = Task.Run(() =>
            {
                try
                {
                    return directory ? Directory.Exists(path) : File.Exists(path);
                }
                catch
                {
                    return false; // an unusable path is simply not there
                }
            });
            Probes[key] = probe;
            probe.ContinueWith(_ =>
            {
                lock (Probes)
                    Probes.Remove(key);
            }, TaskScheduler.Default);
            return probe;
        }
    }

    private static HttpClient CreateClient()
    {
        var c = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        c.DefaultRequestHeaders.TryAddWithoutValidation(
            "User-Agent",
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Anchor/1.0");
        return c;
    }

    /// <summary>
    /// Resolves <paramref name="item"/>'s icon, decoded for a display at
    /// <paramref name="rasterizationScale"/> (physical pixels per DIP — 1.0 at 96 DPI, 2.0 at
    /// 200%). The caller supplies it because the scale belongs to the dock's own monitor, and
    /// two docks on a mixed-DPI setup want different ones.
    /// </summary>
    public static async Task<ImageSource?> LoadIconAsync(DockItem item, double rasterizationScale)
    {
        int decodeSize = TargetDecodeSize(rasterizationScale);
        try
        {
            // 0. A built-in glyph the user picked from the icon picker is final — it needs no
            // resolution at all, and must not be raced by a shell/favicon fetch that would
            // silently overwrite the user's choice with a bitmap once it lands.
            if (!string.IsNullOrEmpty(item.CustomGlyph))
                return null;

            // 1. Explicit user-supplied icon wins — but only if it actually decodes. A path that
            // has since been deleted, or an image the XAML stack can't read, falls through to the
            // normal resolution below rather than leaving the item blank.
            if (!string.IsNullOrWhiteSpace(item.CustomIconPath) && await ProbeAsync(item.CustomIconPath, directory: false))
            {
                var custom = await FromFileAsync(item.CustomIconPath!, decodeSize);
                if (custom is not null)
                    return custom;
                Diag.Log($"IconService: custom icon '{item.CustomIconPath}' didn't decode — using the default");
            }

            // 2. Kind-specific resolution.
            switch (item.Kind)
            {
                case DockItemKind.Application:
                case DockItemKind.File:
                    if (await ProbeAsync(item.Target, directory: false))
                        return await ShellIconAsync(item.Target, decodeSize);
                    // A Start-menu app has no file behind it to ask about — its icon is reached
                    // through the item ID list the shell resolves its AppUserModelID to.
                    if (DockItemFactory.IsAppsFolderTarget(item.Target))
                        return await ShellIconFromParsingNameAsync(item.Target, decodeSize);
                    break;

                case DockItemKind.Folder:
                    if (await ProbeAsync(item.Target, directory: true))
                        return await ShellIconAsync(item.Target, decodeSize);
                    break;

                case DockItemKind.WebLink:
                    return await FaviconAsync(item.Target, decodeSize);
            }
        }
        catch (Exception ex)
        {
            Diag.Log($"IconService: load failed for '{item.Target}': {ex.GetType().Name}: {ex.Message}");
        }
        return null;
    }

    /// <summary>Clears any cached favicon for a web link so the next load re-fetches it.</summary>
    public static void ClearCachedIcon(DockItem item)
    {
        if (item.Kind != DockItemKind.WebLink)
            return;
        if (!Uri.TryCreate(item.Target, UriKind.Absolute, out var uri) || string.IsNullOrEmpty(uri.Host))
            return;

        string cacheFile = Path.Combine(CacheDir, Sanitize(uri.Host) + ".ico");
        try
        {
            if (File.Exists(cacheFile))
                File.Delete(cacheFile);
        }
        catch (Exception ex)
        {
            Diag.Log("IconService cache delete failed: " + ex.Message);
        }
    }

    /// <summary>
    /// Pixel size to decode an icon at: the item's logical size, scaled for the display, then
    /// doubled so magnification (which grows an icon well past its resting size) has real pixels
    /// to draw rather than an upscaled blur.
    /// <para>
    /// The scale is passed in rather than read from <c>Window.Current</c>, which is a UWP API
    /// that always returns null in a WinUI 3 desktop app — reading it here silently pinned every
    /// decode to 1.0 and left icons soft on any display above 100%.
    /// </para>
    /// </summary>
    private static int TargetDecodeSize(double rasterizationScale)
    {
        // Guard the scale rather than trust it: a caller that asks before its window has a DPI
        // yet would otherwise decode at zero.
        double scale = double.IsFinite(rasterizationScale) ? Math.Max(1.0, rasterizationScale) : 1.0;
        return (int)Math.Ceiling(DockMetrics.Icon * scale * 2);
    }

    /// <summary>
    /// Resolves a file, app, or folder's shell icon via pure Win32 — no WinRT Storage broker
    /// involved, so this needs no <c>broadFileSystemAccess</c> capability. Prefers the 256x256
    /// "jumbo" icon (matching Explorer's large-icon views), then extra-large (48px), then the
    /// classic 32x32 icon if the jumbo lookup fails for any reason.
    /// </summary>
    private static async Task<ImageSource?> ShellIconAsync(string path, int decodeSize)
    {
        nint hIcon = TryGetShellIcon(path, NativeMethods.SHIL_JUMBO);
        if (hIcon == nint.Zero)
            hIcon = TryGetShellIcon(path, NativeMethods.SHIL_EXTRALARGE);
        if (hIcon == nint.Zero)
        {
            var info = new NativeMethods.SHFILEINFO();
            nint result = NativeMethods.SHGetFileInfo(
                path, 0, ref info, (uint)Marshal.SizeOf<NativeMethods.SHFILEINFO>(),
                NativeMethods.SHGFI_ICON | NativeMethods.SHGFI_LARGEICON);
            if (result == 0 || info.hIcon == nint.Zero)
                return null;
            hIcon = info.hIcon;
        }

        return await FromIconHandleAsync(hIcon, decodeSize);
    }

    /// <summary>
    /// The same shell icon, for an item that is not a file: a Start-menu app, whose
    /// <c>shell:AppsFolder\…</c> target the shell parses into an item ID list that the image list
    /// answers for exactly as it does for a path.
    /// </summary>
    private static async Task<ImageSource?> ShellIconFromParsingNameAsync(string parsingName, int decodeSize)
    {
        if (NativeMethods.SHParseDisplayName(parsingName, nint.Zero, out nint pidl, 0, out _) != 0 ||
            pidl == nint.Zero)
        {
            Diag.Log($"IconService: the shell doesn't recognize '{parsingName}'");
            return null;
        }

        try
        {
            nint hIcon = TryGetShellIcon(pidl, NativeMethods.SHIL_JUMBO);
            if (hIcon == nint.Zero)
                hIcon = TryGetShellIcon(pidl, NativeMethods.SHIL_EXTRALARGE);
            return hIcon == nint.Zero ? null : await FromIconHandleAsync(hIcon, decodeSize);
        }
        finally
        {
            Marshal.FreeCoTaskMem(pidl);
        }
    }

    /// <summary>Turns an HICON into a decoded image source, and releases the handle either way.</summary>
    private static async Task<ImageSource?> FromIconHandleAsync(nint hIcon, int decodeSize)
    {
        try
        {
            using var icon = System.Drawing.Icon.FromHandle(hIcon);
            using var bitmap = icon.ToBitmap();
            using var ms = new MemoryStream();
            bitmap.Save(ms, System.Drawing.Imaging.ImageFormat.Png);
            return await DecodeAsync(ms.ToArray(), decodeSize);
        }
        finally
        {
            NativeMethods.DestroyIcon(hIcon);
        }
    }

    /// <summary>
    /// Looks up <paramref name="path"/>'s index in the shell's system image list, then resolves
    /// that index against the requested image list size.
    /// </summary>
    private static nint TryGetShellIcon(string path, int imageList)
    {
        var info = new NativeMethods.SHFILEINFO();
        nint listHandle = NativeMethods.SHGetFileInfo(
            path, 0, ref info, (uint)Marshal.SizeOf<NativeMethods.SHFILEINFO>(),
            NativeMethods.SHGFI_SYSICONINDEX);
        return listHandle == nint.Zero ? nint.Zero : IconFromSystemImageList(info.iIcon, imageList);
    }

    /// <summary>The same lookup for an item ID list rather than a path.</summary>
    private static nint TryGetShellIcon(nint pidl, int imageList)
    {
        var info = new NativeMethods.SHFILEINFO();
        nint listHandle = NativeMethods.SHGetFileInfoPidl(
            pidl, 0, ref info, (uint)Marshal.SizeOf<NativeMethods.SHFILEINFO>(),
            NativeMethods.SHGFI_PIDL | NativeMethods.SHGFI_SYSICONINDEX);
        return listHandle == nint.Zero ? nint.Zero : IconFromSystemImageList(info.iIcon, imageList);
    }

    private static nint IconFromSystemImageList(int index, int imageList)
    {
        var iid = NativeMethods.IID_IImageList;
        if (NativeMethods.SHGetImageList(imageList, ref iid, out var shellList) != 0)
            return nint.Zero;

        return shellList.GetIcon(index, NativeMethods.ILD_TRANSPARENT, out nint hIcon) == 0
            ? hIcon
            : nint.Zero;
    }

    /// <summary>Decodes an image file the user pointed at (a custom icon can be any path on
    /// disk). Plain Win32 file I/O — a full-trust process needs no broker capability for it.</summary>
    private static async Task<ImageSource?> FromFileAsync(string path, int decodeSize)
    {
        try
        {
            return await DecodeAsync(await File.ReadAllBytesAsync(path), decodeSize);
        }
        catch (Exception ex)
        {
            Diag.Log($"IconService: direct read failed for '{path}': {ex.GetType().Name}: {ex.Message}");
            return null;
        }
    }

    // ---- Favicons ---------------------------------------------------------

    /// <summary>
    /// Resolves a web link's icon: a disk-cached favicon if present, otherwise fetched from the
    /// site itself (its /favicon.ico), falling back to a favicon service. Returns null (→ globe
    /// glyph) when nothing usable is found or there's no connectivity.
    /// </summary>
    private static async Task<ImageSource?> FaviconAsync(string url, int decodeSize)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || string.IsNullOrEmpty(uri.Host))
            return null;

        string host = uri.Host;
        string scheme = uri.Scheme is "http" or "https" ? uri.Scheme : "https";
        string cacheFile = Path.Combine(CacheDir, Sanitize(host) + ".ico");

        // 1. Cached copy.
        if (File.Exists(cacheFile))
        {
            var cached = await DecodeAsync(await File.ReadAllBytesAsync(cacheFile), decodeSize);
            if (cached is not null)
                return cached;
        }

        // 2. Fetch, preferring the site's own favicon (no third party), then a service fallback.
        foreach (var candidate in new[]
                 {
                     $"{scheme}://{host}/favicon.ico",
                     $"https://icons.duckduckgo.com/ip3/{host}.ico",
                 })
        {
            var bytes = await TryDownloadAsync(candidate);
            if (bytes is null || !LooksLikeImage(bytes))
                continue;

            var image = await DecodeAsync(bytes, decodeSize);
            if (image is null)
                continue;

            TrySaveCache(cacheFile, bytes);
            return image;
        }

        return null;
    }

    private static async Task<byte[]?> TryDownloadAsync(string url)
    {
        try
        {
            using var resp = await Http.GetAsync(url, HttpCompletionOption.ResponseContentRead);
            if (!resp.IsSuccessStatusCode)
                return null;
            var bytes = await resp.Content.ReadAsByteArrayAsync();
            return bytes.Length is > 0 and < 2_000_000 ? bytes : null;
        }
        catch
        {
            return null; // no connectivity / DNS / TLS / timeout — glyph fallback
        }
    }

    /// <summary>Decodes raw bytes into a BitmapImage, confirming the decode actually succeeded
    /// (so a 200-OK HTML error page never gets shown as a broken image).</summary>
    private static async Task<ImageSource?> DecodeAsync(byte[] bytes, int decodeSize = 0)
    {
        try
        {
            var bmp = new BitmapImage();
            if (decodeSize > 0)
                bmp.DecodePixelWidth = bmp.DecodePixelHeight = decodeSize;
            var tcs = new TaskCompletionSource<bool>();
            void OnOpened(object s, Microsoft.UI.Xaml.RoutedEventArgs e) => tcs.TrySetResult(true);
            void OnFailed(object s, Microsoft.UI.Xaml.ExceptionRoutedEventArgs e) => tcs.TrySetResult(false);
            bmp.ImageOpened += OnOpened;
            bmp.ImageFailed += OnFailed;

            using (var ras = new InMemoryRandomAccessStream())
            {
                using (var writer = new DataWriter(ras))
                {
                    writer.WriteBytes(bytes);
                    await writer.StoreAsync();
                    await writer.FlushAsync();
                    writer.DetachStream();
                }
                ras.Seek(0);
                await bmp.SetSourceAsync(ras);
            }

            bool ok = await tcs.Task;
            bmp.ImageOpened -= OnOpened;
            bmp.ImageFailed -= OnFailed;
            return ok ? bmp : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Magic-byte sniff: cheaply reject HTML/text (e.g. a soft-404) before decoding.</summary>
    private static bool LooksLikeImage(byte[] b)
    {
        if (b.Length < 4)
            return false;
        // ICO / CUR
        if (b[0] == 0x00 && b[1] == 0x00 && (b[2] == 0x01 || b[2] == 0x02) && b[3] == 0x00) return true;
        // PNG
        if (b[0] == 0x89 && b[1] == 0x50 && b[2] == 0x4E && b[3] == 0x47) return true;
        // JPEG
        if (b[0] == 0xFF && b[1] == 0xD8 && b[2] == 0xFF) return true;
        // GIF
        if (b[0] == 0x47 && b[1] == 0x49 && b[2] == 0x46 && b[3] == 0x38) return true;
        // BMP
        if (b[0] == 0x42 && b[1] == 0x4D) return true;
        // WEBP (RIFF....WEBP)
        if (b.Length >= 12 && b[0] == 0x52 && b[1] == 0x49 && b[2] == 0x46 && b[3] == 0x46 &&
            b[8] == 0x57 && b[9] == 0x45 && b[10] == 0x42 && b[11] == 0x50) return true;
        return false;
    }

    private static void TrySaveCache(string path, byte[] bytes)
    {
        try
        {
            Directory.CreateDirectory(CacheDir);
            File.WriteAllBytes(path, bytes);
        }
        catch (Exception ex)
        {
            Diag.Log("IconService cache write failed: " + ex.Message);
        }
    }

    private static string Sanitize(string host)
    {
        foreach (char c in Path.GetInvalidFileNameChars())
            host = host.Replace(c, '_');
        return host;
    }
}
