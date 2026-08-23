namespace YouTubeTool.Models;

public class Channel
{
    public int Id { get; set; }
    public string YouTubeChannelId { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string? ThumbnailUrl { get; set; }
    public DateTime? LastFetchedAt { get; set; }
    public VideoSortOrder VideoSortOrder { get; set; } = VideoSortOrder.OldestFirst;

    // Per-channel view options, toggled from the channel right-click menu. Each one reads as
    // "show this kind of video". Shorts default on (they're part of a channel's normal output);
    // watched and members-only default off.
    public bool ShowShorts { get; set; } = true;
    public bool ShowWatched { get; set; }
    // Also drives fetching: when true, Refresh pulls this channel's members-only videos
    // (needs a signed-in YouTube session — see YouTubeService.FetchMembersOnlyVideosAsync).
    public bool ShowMembersOnly { get; set; }
    public ICollection<Video> Videos { get; set; } = [];
    public ICollection<ChannelList> Lists { get; set; } = [];
}
