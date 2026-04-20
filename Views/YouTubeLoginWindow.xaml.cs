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
            DialogResult = true;
        }
        else
        {
            MessageBox.Show(
                "Not signed in yet. Please sign into YouTube in the browser above, then click Done.",
                "Not Signed In", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }
}
