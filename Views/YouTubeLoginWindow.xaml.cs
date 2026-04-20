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
            // Dump ytcfg diagnostics and extract the active channel ID.
            try
            {
                var currentUrl = WebView.CoreWebView2.Source;

                // Collect several candidate keys for the active channel
                var scriptResult = await WebView.CoreWebView2.ExecuteScriptAsync(@"
(function(){
    try {
        var ctx = window.ytcfg ? window.ytcfg.get('INNERTUBE_CONTEXT') : null;
        return JSON.stringify({
            url: location.href,
            onBehalfOfUser: ctx?.user?.onBehalfOfUser ?? '',
            delegatedSessionId: window.ytcfg ? (window.ytcfg.get('DELEGATED_SESSION_ID') ?? '') : '',
            datasyncId: window.ytcfg ? (window.ytcfg.get('DATASYNC_ID') ?? '') : '',
            pageId: window.ytcfg ? (window.ytcfg.get('PAGE_CL') ?? '') : '',
            hasYtcfg: !!window.ytcfg
        });
    } catch(e) { return JSON.stringify({error: e.toString()}); }
})()");

                // scriptResult is a JSON-encoded string — unwrap the outer quotes
                var inner = System.Text.Json.JsonSerializer.Deserialize<string>(scriptResult) ?? "{}";

                // Save full diagnostic dump
                File.WriteAllText(Path.Combine(_userDataPath, "ytcfg_dump.json"), inner);

                // Extract onBehalfOfUser from the dump
                using var doc = System.Text.Json.JsonDocument.Parse(inner);
                var onBehalfOf = "";
                if (doc.RootElement.TryGetProperty("onBehalfOfUser", out var obu))
                    onBehalfOf = obu.GetString() ?? "";
                // Fallback: try delegatedSessionId
                if (string.IsNullOrEmpty(onBehalfOf) &&
                    doc.RootElement.TryGetProperty("delegatedSessionId", out var dsi))
                    onBehalfOf = dsi.GetString() ?? "";

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
