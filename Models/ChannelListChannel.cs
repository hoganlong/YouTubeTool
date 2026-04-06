namespace YouTubeTool.Models;

public class ChannelListChannel
{
    public int ChannelsId { get; set; }
    public int ListsId { get; set; }
    public int SortOrder { get; set; }
    public Channel Channel { get; set; } = null!;
    public ChannelList List { get; set; } = null!;
}
