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
    // Returns an empty dict if no session exists yet.
    public Task<Dictionary<string, string>> TryGetStoredCookiesAsync() => ReadCookiesAsync();

    // Returns YouTube cookies from our persistent WebView2 session.
    // Shows the login window if not yet logged in.
    public async Task<Dictionary<string, string>> GetYouTubeCookiesAsync(Window owner)
    {
        var cookies = await ReadCookiesAsync();
        if (cookies.ContainsKey("SAPISID"))
            return cookies;

        // Not logged in — show the login window
        var loginWin = new Views.YouTubeLoginWindow(UserDataPath) { Owner = owner };
        if (loginWin.ShowDialog() != true)
            return [];

        return await ReadCookiesAsync();
    }

    // Spins up a hidden WebView2 with our user data folder, reads YouTube cookies, disposes it.
    private static async Task<Dictionary<string, string>> ReadCookiesAsync()
    {
        Dictionary<string, string>? result = null;
        Exception? error = null;

        await WithWebViewAsync(async webView =>
        {
            var raw = await webView.CoreWebView2.CookieManager
                .GetCookiesAsync("https://www.youtube.com");
            result = raw.ToDictionary(c => c.Name, c => c.Value);
        });

        if (error != null) throw error;
        return result ?? [];
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
