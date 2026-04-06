namespace YouTubeTool.Models;

public class ChannelList
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public int SortOrder { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public ICollection<Channel> Channels { get; set; } = [];
}
