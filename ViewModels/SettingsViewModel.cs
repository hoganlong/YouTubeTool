using System.Windows.Input;
using YouTubeTool.Models;
using YouTubeTool.Services;

namespace YouTubeTool.ViewModels;

public class SettingsViewModel : BaseViewModel
{
    private readonly SettingsService _settingsService;
    private readonly YouTubeService _youTubeService;
    private readonly GoogleAuthService _authService;
    private readonly WebView2CookieService _webView2Cookies;

    private string _apiKey = string.Empty;
    private string _oAuthClientId = string.Empty;
    private string _oAuthClientSecret = string.Empty;
    private string _testResult = string.Empty;
    private string _signInStatus = string.Empty;
    private string _youTubeSessionStatus = string.Empty;
    private int _maxVideos = 50;
    private double _uiScale = 1.0;

    public string ApiKey { get => _apiKey; set => SetProperty(ref _apiKey, value); }
    public string OAuthClientId { get => _oAuthClientId; set => SetProperty(ref _oAuthClientId, value); }
    public string OAuthClientSecret { get => _oAuthClientSecret; set => SetProperty(ref _oAuthClientSecret, value); }
    public int MaxVideos { get => _maxVideos; set => SetProperty(ref _maxVideos, value); }
    public double UiScale { get => _uiScale; set => SetProperty(ref _uiScale, value); }
    public string TestResult { get => _testResult; set => SetProperty(ref _testResult, value); }
    public string SignInStatus { get => _signInStatus; set => SetProperty(ref _signInStatus, value); }
    public string YouTubeSessionStatus { get => _youTubeSessionStatus; set => SetProperty(ref _youTubeSessionStatus, value); }

    public ICommand SaveCommand { get; }
    public ICommand TestApiKeyCommand { get; }
    public ICommand SignInCommand { get; }
    public ICommand SignOutCommand { get; }
    public ICommand ClearYouTubeSessionCommand { get; }

    public event Action? CloseRequested;

    public SettingsViewModel(SettingsService settingsService, YouTubeService youTubeService, GoogleAuthService authService, WebView2CookieService webView2Cookies)
    {
        _settingsService = settingsService;
        _youTubeService = youTubeService;
        _authService = authService;
        _webView2Cookies = webView2Cookies;

        var settings = settingsService.LoadSettings();
        _apiKey = settings.YouTubeApiKey;
        _oAuthClientId = settings.OAuthClientId;
        _oAuthClientSecret = settings.OAuthClientSecret;
        _maxVideos = settings.MaxVideosPerChannel;
        _uiScale = settings.UiScale;

        UpdateSignInStatus();

        SaveCommand = new RelayCommand(Save);
        TestApiKeyCommand = new AsyncRelayCommand(TestApiKeyAsync);
        SignInCommand = new AsyncRelayCommand(SignInAsync, () => !_authService.IsSignedIn);
        SignOutCommand = new AsyncRelayCommand(SignOutAsync, () => _authService.IsSignedIn);
        ClearYouTubeSessionCommand = new RelayCommand(ClearYouTubeSession);
    }

    private void UpdateSignInStatus()
    {
        SignInStatus = _authService.IsSignedIn
            ? "✓ Signed in to Google"
            : "Not signed in";
    }

    private void Save()
    {
        _settingsService.SaveSettings(new AppSettings
        {
            YouTubeApiKey = ApiKey,
            OAuthClientId = OAuthClientId,
            OAuthClientSecret = OAuthClientSecret,
            MaxVideosPerChannel = MaxVideos,
            UiScale = UiScale
        });
        CloseRequested?.Invoke();
    }

    private async Task TestApiKeyAsync()
    {
        TestResult = "Testing...";
        var valid = await _youTubeService.ValidateApiKeyAsync(ApiKey);
        TestResult = valid ? "✓ API key is valid!" : "✗ Invalid API key or network error.";
    }

    private async Task SignInAsync()
    {
        if (string.IsNullOrWhiteSpace(OAuthClientId) || string.IsNullOrWhiteSpace(OAuthClientSecret))
        {
            SignInStatus = "✗ Enter Client ID and Client Secret first, then Save.";
            return;
        }

        SignInStatus = "Opening browser for Google sign-in...";
        try
        {
            // Save credentials first so they're persisted
            _settingsService.SaveSettings(new AppSettings
            {
                YouTubeApiKey = ApiKey,
                OAuthClientId = OAuthClientId,
                OAuthClientSecret = OAuthClientSecret,
                MaxVideosPerChannel = MaxVideos
            });

            await _authService.SignInAsync(OAuthClientId, OAuthClientSecret);
            UpdateSignInStatus();
        }
        catch (Exception ex)
        {
            SignInStatus = $"✗ Sign-in failed: {ex.Message}";
        }
    }

    private async Task SignOutAsync()
    {
        await _authService.SignOutAsync();
        UpdateSignInStatus();
    }

    private void ClearYouTubeSession()
    {
        _webView2Cookies.SignOut();
        YouTubeSessionStatus = "✓ Session cleared — you'll be prompted to sign in again next time.";
    }
}
