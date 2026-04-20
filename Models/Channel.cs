namespace YouTubeTool.Models;

public class Channel
{
    public int Id { get; set; }
    public string YouTubeChannelId { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string? ThumbnailUrl { get; set; }
    public DateTime? LastFetchedAt { get; set; }
    public VideoSortOrder VideoSortOrder { get; set; } = VideoSortOrder.OldestFirst;
    public ICollection<Video> Videos { get; set; } = [];
    public ICollection<ChannelList> Lists { get; set; } = [];
}
