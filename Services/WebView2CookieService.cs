using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace YouTubeTool.Services;

public class WebView2CookieService
{
    private static readonly string UserDataPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "YouTubeTool", "webview2");

    // Returns the YouTube channel ID to act as (brand account), or null for the primary account.
    // Saved by the login window when the user switches channels before clicking Done.
    public string? TryGetOnBehalfOfUser()
    {
        var path = Path.Combine(UserDataPath, "on_behalf_of.txt");
        try
        {
            var v = File.Exists(path) ? File.ReadAllText(path).Trim() : null;
            return string.IsNullOrEmpty(v) ? null : v;
        }
        catch { return null; }
    }

    // Clears the session by deleting all cookies via WebView2's own API.
    // This is more reliable than deleting files — WebView2 background processes
    // often hold file locks on the user data folder, causing silent deletion failures.
    public async Task SignOutAsync()
    {
        try
        {
            await WithWebViewAsync(webView =>
            {
                webView.CoreWebView2.CookieManager.DeleteAllCookies();
                return Task.CompletedTask;
            });
        }
        catch { }

        // Clear the brand account context
        try { File.Delete(Path.Combine(UserDataPath, "on_behalf_of.txt")); } catch { }
    }

    // Returns stored WebView2 cookies without showing a login window.
    // Returns an empty dict if no session exists yet. Passive check — does NOT
    // refresh the session (used only for the Settings "session status" display).
    public Task<Dictionary<string, string>> TryGetStoredCookiesAsync() => ReadCookiesAsync(refreshSession: false);

    // Returns YouTube cookies from our persistent WebView2 session.
    // Shows the login window if not yet logged in.
    public async Task<Dictionary<string, string>> GetYouTubeCookiesAsync(Window owner)
    {
        var cookies = await ReadCookiesAsync(refreshSession: true);
        if (cookies.ContainsKey("SAPISID"))
            return cookies;

        // Not logged in — show the login window
        var loginWin = new Views.YouTubeLoginWindow(UserDataPath) { Owner = owner };
        if (loginWin.ShowDialog() != true)
            return [];

        return await ReadCookiesAsync(refreshSession: true);
    }

    // Spins up a hidden WebView2 with our user data folder, reads YouTube cookies, disposes it.
    //
    // When refreshSession is true, navigate to youtube.com first and wait for it to load.
    // Google rotates the __Secure-1PSIDTS/__Secure-3PSIDTS session tokens every few hours;
    // a passive cookie read lets them go stale, so the next InnerTube call comes back as a
    // signed-out feed (HTTP 200, loggedOut=true) even though all cookies are still on disk.
    // Loading the page makes Google re-issue fresh rotating tokens via Set-Cookie, which
    // WebView2 persists — keeping the session alive the way an open browser tab would.
    // The passive status check skips this so it stays instant and side-effect-free.
    private static async Task<Dictionary<string, string>> ReadCookiesAsync(bool refreshSession)
    {
        Dictionary<string, string>? result = null;

        await WithWebViewAsync(async webView =>
        {
            if (refreshSession)
                await RefreshSessionAsync(webView);

            var raw = await webView.CoreWebView2.CookieManager
                .GetCookiesAsync("https://www.youtube.com");

            // WebView2 can return the same cookie name under multiple domain/path scopes
            // (e.g. __Secure-YNID on both .youtube.com and a subdomain). ToDictionary would
            // throw on the duplicate key, so build the map with last-wins instead.
            result = new Dictionary<string, string>();
            foreach (var c in raw)
                result[c.Name] = c.Value;
        });

        return result ?? [];
    }

    // Navigate to youtube.com and wait for the load to complete (with a timeout so an offline
    // or hung load can't block a sync forever), then give Google a moment to write any deferred
    // rotating-token cookies before we read. Failures here are non-fatal: we fall through to
    // reading whatever cookies are already stored.
    private static async Task RefreshSessionAsync(WebView2 webView)
    {
        try
        {
            var navDone = new TaskCompletionSource();
            void OnNavigationCompleted(object? s, CoreWebView2NavigationCompletedEventArgs e)
                => navDone.TrySetResult();

            webView.CoreWebView2.NavigationCompleted += OnNavigationCompleted;
            try
            {
                webView.CoreWebView2.Navigate("https://www.youtube.com");
                await Task.WhenAny(navDone.Task, Task.Delay(TimeSpan.FromSeconds(20)));
            }
            finally
            {
                webView.CoreWebView2.NavigationCompleted -= OnNavigationCompleted;
            }

            // Deferred Set-Cookie for the rotated tokens can land shortly after the main
            // document finishes; a short grace period lets it settle before we read.
            await Task.Delay(TimeSpan.FromSeconds(2));
        }
        catch { }
    }

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint flags);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);

    [DllImport("user32.dll")]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO info);

    [DllImport("user32.dll")]
    private static extern bool ClientToScreen(IntPtr hWnd, ref POINT point);

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X, Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MONITORINFO { public int cbSize; public RECT rcMonitor, rcWork; public int dwFlags; }

    private const uint MONITOR_DEFAULTTONEAREST = 2;
    private const uint SWP_NOSIZE = 0x0001, SWP_NOZORDER = 0x0004, SWP_NOACTIVATE = 0x0010;

    // Helper: creates a hidden WebView2 window, runs an action, then closes it.
    //
    // The host window used to be parked unowned at (-9999,-9999) and shown with activation.
    // The taskbar places an app's button by the monitor its windows are on, and an off-screen
    // window resolves to whichever monitor is "nearest" — so the main window's taskbar button
    // would intermittently jump to the other monitor. Now the host is owned by the main window,
    // never activates, and sits at the main window's top-left content pixel (same monitor).
    private static async Task WithWebViewAsync(Func<WebView2, Task> action)
    {
        var tcs = new TaskCompletionSource();

        await Application.Current.Dispatcher.InvokeAsync(async () =>
        {
            var owner = Application.Current.MainWindow;
            var win = new Window
            {
                Width = 1, Height = 1,
                WindowStyle = WindowStyle.None,
                ShowInTaskbar = false,
                ShowActivated = false,
                ResizeMode = ResizeMode.NoResize
            };

            // A minimized owner hides its owned windows, and WebView2 may not load in a hidden
            // one — so only take ownership while the main window is actually on screen.
            bool ownerOnScreen = owner is { IsVisible: true } && owner.WindowState != WindowState.Minimized;
            if (ownerOnScreen) win.Owner = owner;

            // Create the HWND without showing it, move it, then show — so it never appears
            // anywhere else first. Positioned in physical pixels to sidestep per-monitor DPI.
            var hwnd = new WindowInteropHelper(win).EnsureHandle();
            if (owner != null && TryGetHostPosition(owner, ownerOnScreen, out var x, out var y))
                SetWindowPos(hwnd, IntPtr.Zero, x, y, 0, 0, SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE);

            var webView = new WebView2();
            win.Content = webView;
            win.Show();

            try
            {
                Directory.CreateDirectory(UserDataPath);
                var env = await CoreWebView2Environment.CreateAsync(null, UserDataPath);
                await webView.EnsureCoreWebView2Async(env);
                await action(webView);
                tcs.SetResult();
            }
            catch (Exception ex)
            {
                tcs.SetException(ex);
            }
            finally
            {
                win.Close();
            }
        });

        await tcs.Task;
    }

    // Where the hidden host goes, in physical screen pixels. On screen: the main window's
    // top-left *content* pixel — not Left/Top (those are restore bounds when maximized) and not
    // the outer frame (a maximized frame overhangs the screen edge, possibly onto the other
    // monitor). Minimized: the top-left of the monitor the main window will restore onto.
    private static bool TryGetHostPosition(Window owner, bool ownerOnScreen, out int x, out int y)
    {
        x = y = 0;
        var ownerHwnd = new WindowInteropHelper(owner).Handle;
        if (ownerHwnd == IntPtr.Zero) return false;

        if (ownerOnScreen)
        {
            var pt = new POINT();
            if (!ClientToScreen(ownerHwnd, ref pt)) return false;
            (x, y) = (pt.X, pt.Y);
            return true;
        }

        var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        if (!GetMonitorInfo(MonitorFromWindow(ownerHwnd, MONITOR_DEFAULTTONEAREST), ref info)) return false;
        (x, y) = (info.rcWork.Left, info.rcWork.Top);
        return true;
    }
}
