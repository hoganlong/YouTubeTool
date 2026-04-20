using System.Windows;
using System.Windows.Controls;
using YouTubeTool.Services;
using YouTubeTool.ViewModels;

namespace YouTubeTool.Views;

public partial class SettingsWindow : Window
{
    private readonly SettingsViewModel _vm;

    public SettingsWindow()
    {
        InitializeComponent();
        _vm = new SettingsViewModel(App.SettingsService, App.YouTubeService, App.GoogleAuthService, App.WebView2CookieService);
        _vm.CloseRequested += () => DialogResult = true;
        DataContext = _vm;
        ApiKeyBox.Password = _vm.ApiKey;
        ClientSecretBox.Password = _vm.OAuthClientSecret;
    }

    private void ApiKeyBox_PasswordChanged(object sender, RoutedEventArgs e)
        => _vm.ApiKey = ((PasswordBox)sender).Password;

    private void ClientSecretBox_PasswordChanged(object sender, RoutedEventArgs e)
        => _vm.OAuthClientSecret = ((PasswordBox)sender).Password;
}
