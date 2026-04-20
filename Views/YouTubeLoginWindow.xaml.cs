using System.IO;
using System.Windows;
using Microsoft.Web.WebView2.Core;

namespace YouTubeTool.Views;

public partial class YouTubeLoginWindow : Window
{
    private readonly string _userDataPath;

    public YouTubeLoginWindow(string userDataPath)
    {
        _userDataPath = userDataPath;
        InitializeComponent();
        Loaded += OnLoaded;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        Directory.CreateDirectory(_userDataPath);
        var env = await CoreWebView2Environment.CreateAsync(null, _userDataPath);
        await WebView.EnsureCoreWebView2Async(env);
        WebView.CoreWebView2.Navigate("https://accounts.google.com/AccountChooser?continue=https://www.youtube.com");
    }

    private async void Done_Click(object sender, RoutedEventArgs e)
    {
        var cookies = await WebView.CoreWebView2.CookieManager
            .GetCookiesAsync("https://www.youtube.com");

        if (cookies.Any(c => c.Name == "SAPISID"))
        {
            // Read the active channel context from YouTube's JS config.
            // onBehalfOfUser is set when the user has switched to a brand account (e.g. mitelit).
            // It must be included in InnerTube requests to fetch that channel's subscriptions.
            try
            {
                var result = await WebView.CoreWebView2.ExecuteScriptAsync(
                    "(function(){ try { return window.ytcfg.get('INNERTUBE_CONTEXT')?.user?.onBehalfOfUser ?? ''; } catch(e){ return ''; } })()");
                // ExecuteScriptAsync returns a JSON-encoded string — deserialise it
                var onBehalfOf = System.Text.Json.JsonSerializer.Deserialize<string>(result) ?? "";
                File.WriteAllText(Path.Combine(_userDataPath, "on_behalf_of.txt"), onBehalfOf);
            }
            catch { }

            DialogResult = true;
        }
        else
        {
            var currentUrl = WebView.CoreWebView2.Source;
            var cookieNames = cookies.Count > 0
                ? string.Join(", ", cookies.Select(c => c.Name))
                : "(none)";

            MessageBox.Show(
                $"Not signed in yet — SAPISID cookie not found.\n\n" +
                $"Current page: {currentUrl}\n" +
                $"YouTube cookies found: {cookieNames}\n\n" +
                "Please sign into YouTube in the browser above.\n" +
                "If you want to use a specific YouTube channel, click your profile picture (top-right on YouTube) and switch to that channel before clicking Done.",
                "Not Signed In", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }
}
