using System.IO;
using Google.Apis.Auth.OAuth2;
using Google.Apis.Util.Store;
using GoogleYT = Google.Apis.YouTube.v3;

namespace YouTubeTool.Services;

public class GoogleAuthService
{
    private static readonly string TokenPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "YouTubeTool", "oauth_tokens");

    private UserCredential? _credential;

    public bool IsSignedIn => _credential != null;

    // Called on startup — silently loads saved token without opening browser
    public async Task TryRestoreSessionAsync(string clientId, string clientSecret)
    {
        if (string.IsNullOrWhiteSpace(clientId) || string.IsNullOrWhiteSpace(clientSecret))
            return;

        try
        {
            // Check if a token file actually exists before trying to load
            if (!Directory.Exists(TokenPath)) return;

            var secrets = new ClientSecrets { ClientId = clientId, ClientSecret = clientSecret };
            _credential = await GoogleWebAuthorizationBroker.AuthorizeAsync(
                secrets,
                [GoogleYT.YouTubeService.Scope.Youtube],
                "user",
                CancellationToken.None,
                new FileDataStore(TokenPath, true));
        }
        catch
        {
            _credential = null;
        }
    }

    // Called when user clicks Sign In — opens browser if needed
    public async Task<bool> SignInAsync(string clientId, string clientSecret)
    {
        if (string.IsNullOrWhiteSpace(clientId) || string.IsNullOrWhiteSpace(clientSecret))
            throw new ArgumentException("OAuth Client ID and Client Secret are required.");

        var secrets = new ClientSecrets { ClientId = clientId, ClientSecret = clientSecret };
        _credential = await GoogleWebAuthorizationBroker.AuthorizeAsync(
            secrets,
            [GoogleYT.YouTubeService.Scope.Youtube],
            "user",
            CancellationToken.None,
            new FileDataStore(TokenPath, true));

        return _credential != null;
    }

    public async Task SignOutAsync()
    {
        if (_credential != null)
        {
            try { await _credential.RevokeTokenAsync(CancellationToken.None); } catch { }
            _credential = null;
        }

        if (Directory.Exists(TokenPath))
            Directory.Delete(TokenPath, true);
    }

    public UserCredential? GetCredential() => _credential;
}
