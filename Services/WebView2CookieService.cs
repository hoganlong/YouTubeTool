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
        var tcs = new TaskCompletionSource<Dictionary<string, string>>();

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

                var raw = await webView.CoreWebView2.CookieManager
                    .GetCookiesAsync("https://www.youtube.com");

                tcs.SetResult(raw.ToDictionary(c => c.Name, c => c.Value));
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

        return await tcs.Task;
    }
}
