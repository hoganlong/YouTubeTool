namespace YouTubeTool.Models;

public class AppSettings
{
    public string YouTubeApiKey { get; set; } = string.Empty;
    public int MaxVideosPerChannel { get; set; } = 50;
    public string OAuthClientId { get; set; } = string.Empty;
    public string OAuthClientSecret { get; set; } = string.Empty;
}
