namespace YouTubeTool.Models;

public class WatchHistoryEntry
{
    public int Id { get; set; }
    public string YouTubeVideoId { get; set; } = string.Empty;
    public DateTime ImportedAt { get; set; } = DateTime.UtcNow;
}
