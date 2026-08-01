using System.IO;
using System.Windows;
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

    // Helper: creates a hidden WebView2 window, runs an action, then closes it.
    private static async Task WithWebViewAsync(Func<WebView2, Task> action)
    {
        var tcs = new TaskCompletionSource();

        await Application.Current.Dispatcher.InvokeAsync(async () =>
        {
            var win = new Window
            {
                Width = 1, Height = 1,
                WindowStyle = WindowStyle.None,
                ShowInTaskbar = false,
                Left = -9999, Top = -9999,
                ResizeMode = ResizeMode.NoResize
            };

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
}
