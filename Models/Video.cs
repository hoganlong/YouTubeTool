namespace YouTubeTool.Models;

public enum VideoStatus
{
    Unwatched = 0,
    Watched = 1,
    DontWatch = 2,
    NotInterested = 3
}

public enum VideoSortOrder
{
    OldestFirst = 0,
    NewestFirst = 1
}

public class Video
{
    public int Id { get; set; }
    public string YouTubeVideoId { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string? ThumbnailUrl { get; set; }
    public DateTime PublishedAt { get; set; }
    public VideoStatus Status { get; set; } = VideoStatus.Unwatched;
    public bool IsShort { get; set; }
    public bool IsStarred { get; set; }
    public int ChannelId { get; set; }
    public Channel Channel { get; set; } = null!;
}
