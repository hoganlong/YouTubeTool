using Microsoft.EntityFrameworkCore;
using YouTubeTool.Data;
using YouTubeTool.Models;
using YouTubeServiceNS = YouTubeTool.Services;

namespace YouTubeTool.Services;

public class DatabaseService(IDbContextFactory<AppDbContext> factory)
{
    public async Task<List<ChannelList>> GetAllListsAsync()
    {
        await using var db = await factory.CreateDbContextAsync();
        return await db.ChannelLists.OrderBy(l => l.SortOrder).ThenBy(l => l.Name).ToListAsync();
    }

    public async Task<List<Channel>> GetChannelsForListAsync(int listId)
    {
        await using var db = await factory.CreateDbContextAsync();
        return await db.Set<ChannelListChannel>()
            .Where(j => j.ListsId == listId)
            .OrderBy(j => j.SortOrder)
            .Select(j => j.Channel)
            .ToListAsync();
    }

    public async Task<Dictionary<int, int>> GetUnwatchedCountsForListAsync(int listId)
    {
        await using var db = await factory.CreateDbContextAsync();
        var channelIds = await db.ChannelLists
            .Where(l => l.Id == listId)
            .SelectMany(l => l.Channels)
            .Select(c => c.Id)
            .ToListAsync();

        return await db.Videos
            .Where(v => channelIds.Contains(v.ChannelId) && v.Status == VideoStatus.Unwatched)
            .GroupBy(v => v.ChannelId)
            .Select(g => new { ChannelId = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.ChannelId, x => x.Count);
    }

    public async Task<List<Video>> GetUnwatchedVideosForListAsync(int listId)
    {
        await using var db = await factory.CreateDbContextAsync();
        var channelIds = await db.ChannelLists
            .Where(l => l.Id == listId)
            .SelectMany(l => l.Channels)
            .Select(c => c.Id)
            .ToListAsync();

        return await db.Videos
            .Where(v => channelIds.Contains(v.ChannelId) && v.Status == VideoStatus.Unwatched)
            .Include(v => v.Channel)
            .OrderBy(v => v.PublishedAt)
            .ToListAsync();
    }

    public async Task<List<Video>> GetUnwatchedVideosForChannelAsync(int channelId)
    {
        await using var db = await factory.CreateDbContextAsync();
        return await db.Videos
            .Where(v => v.ChannelId == channelId && v.Status == VideoStatus.Unwatched)
            .Include(v => v.Channel)
            .OrderBy(v => v.PublishedAt)
            .ToListAsync();
    }

    public async Task<ChannelList> AddListAsync(string name)
    {
        await using var db = await factory.CreateDbContextAsync();
        var maxOrder = await db.ChannelLists.Select(l => (int?)l.SortOrder).MaxAsync() ?? -1;
        var list = new ChannelList { Name = name, SortOrder = maxOrder + 1 };
        db.ChannelLists.Add(list);
        await db.SaveChangesAsync();
        return list;
    }

    public async Task RenameListAsync(int listId, string name)
    {
        await using var db = await factory.CreateDbContextAsync();
        var list = await db.ChannelLists.FindAsync(listId);
        if (list == null) return;
        list.Name = name;
        await db.SaveChangesAsync();
    }

    public async Task DeleteListAsync(int listId)
    {
        await using var db = await factory.CreateDbContextAsync();
        var list = await db.ChannelLists.FindAsync(listId);
        if (list == null) return;
        db.ChannelLists.Remove(list);
        await db.SaveChangesAsync();
    }

    public async Task<List<Channel>> GetAllChannelsAsync()
    {
        await using var db = await factory.CreateDbContextAsync();
        return await db.Channels.ToListAsync();
    }

    public async Task<Channel?> GetChannelByYouTubeIdAsync(string ytChannelId)
    {
        await using var db = await factory.CreateDbContextAsync();
        return await db.Channels.FirstOrDefaultAsync(c => c.YouTubeChannelId == ytChannelId);
    }

    public async Task<Channel> AddChannelToListAsync(int listId, ChannelInfo info)
    {
        await using var db = await factory.CreateDbContextAsync();

        var channel = await db.Channels.FirstOrDefaultAsync(c => c.YouTubeChannelId == info.YouTubeChannelId);
        if (channel == null)
        {
            channel = new Channel
            {
                YouTubeChannelId = info.YouTubeChannelId,
                Name = info.Name,
                ThumbnailUrl = info.ThumbnailUrl
            };
            db.Channels.Add(channel);
            await db.SaveChangesAsync();
        }

        var alreadyInList = await db.Set<ChannelListChannel>()
            .AnyAsync(j => j.ListsId == listId && j.ChannelsId == channel.Id);

        if (!alreadyInList)
        {
            var maxOrder = await db.Set<ChannelListChannel>()
                .Where(j => j.ListsId == listId)
                .Select(j => (int?)j.SortOrder)
                .MaxAsync() ?? -1;

            db.Set<ChannelListChannel>().Add(new ChannelListChannel
            {
                ChannelsId = channel.Id,
                ListsId = listId,
                SortOrder = maxOrder + 1
            });
            await db.SaveChangesAsync();
        }

        return channel;
    }

    public async Task RemoveChannelFromListAsync(int listId, int channelId)
    {
        await using var db = await factory.CreateDbContextAsync();
        var list = await db.ChannelLists.Include(l => l.Channels).FirstAsync(l => l.Id == listId);
        var channel = list.Channels.FirstOrDefault(c => c.Id == channelId);
        if (channel != null)
        {
            list.Channels.Remove(channel);
            await db.SaveChangesAsync();
        }
    }

    public async Task<List<Video>> GetAllVideosForListAsync(int listId)
    {
        await using var db = await factory.CreateDbContextAsync();
        var channelIds = await db.ChannelLists
            .Where(l => l.Id == listId)
            .SelectMany(l => l.Channels)
            .Select(c => c.Id)
            .ToListAsync();

        return await db.Videos
            .Where(v => channelIds.Contains(v.ChannelId))
            .Include(v => v.Channel)
            .OrderBy(v => v.PublishedAt)
            .ToListAsync();
    }

    public async Task<List<Video>> GetAllVideosForChannelAsync(int channelId)
    {
        await using var db = await factory.CreateDbContextAsync();
        return await db.Videos
            .Where(v => v.ChannelId == channelId)
            .Include(v => v.Channel)
            .OrderBy(v => v.PublishedAt)
            .ToListAsync();
    }

    public async Task MarkAllWatchedForListAsync(int listId)
    {
        await using var db = await factory.CreateDbContextAsync();
        var channelIds = await db.ChannelLists
            .Where(l => l.Id == listId)
            .SelectMany(l => l.Channels)
            .Select(c => c.Id)
            .ToListAsync();

        await db.Videos
            .Where(v => channelIds.Contains(v.ChannelId) && v.Status == VideoStatus.Unwatched)
            .ExecuteUpdateAsync(s => s.SetProperty(v => v.Status, VideoStatus.Watched));
    }

    public async Task MarkAllWatchedForChannelAsync(int channelId)
    {
        await using var db = await factory.CreateDbContextAsync();
        await db.Videos
            .Where(v => v.ChannelId == channelId && v.Status == VideoStatus.Unwatched)
            .ExecuteUpdateAsync(s => s.SetProperty(v => v.Status, VideoStatus.Watched));
    }

    public async Task UpdateVideoStatusAsync(int videoId, VideoStatus status)
    {
        await using var db = await factory.CreateDbContextAsync();
        await db.Videos
            .Where(v => v.Id == videoId)
            .ExecuteUpdateAsync(s => s.SetProperty(v => v.Status, status));
    }

    public async Task UpsertVideosAsync(int channelId, IEnumerable<VideoInfo> videoInfos)
    {
        await using var db = await factory.CreateDbContextAsync();

        var incomingList = videoInfos.ToList();
        var incomingIds = incomingList.Select(v => v.YouTubeVideoId).ToList();

        var existingVideoIds = await db.Videos
            .Where(v => v.ChannelId == channelId)
            .Select(v => v.YouTubeVideoId)
            .ToHashSetAsync();

        // Check which incoming IDs are already in the watch history table
        var alreadyWatchedIds = await db.WatchHistory
            .Where(w => incomingIds.Contains(w.YouTubeVideoId))
            .Select(w => w.YouTubeVideoId)
            .ToHashSetAsync();

        // Insert new videos
        var newVideos = incomingList
            .Where(v => !existingVideoIds.Contains(v.YouTubeVideoId))
            .Select(v => new Video
            {
                YouTubeVideoId = v.YouTubeVideoId,
                Title = v.Title,
                ThumbnailUrl = v.ThumbnailUrl,
                PublishedAt = v.PublishedAt,
                IsShort = v.IsShort,
                ChannelId = channelId,
                Status = alreadyWatchedIds.Contains(v.YouTubeVideoId)
                    ? VideoStatus.Watched
                    : VideoStatus.Unwatched
            })
            .ToList();

        if (newVideos.Count > 0)
            db.Videos.AddRange(newVideos);

        // Update thumbnail URLs and IsShort for existing videos
        var incomingLookup = incomingList
            .ToDictionary(v => v.YouTubeVideoId);

        var existingVideos = await db.Videos
            .Where(v => v.ChannelId == channelId && existingVideoIds.Contains(v.YouTubeVideoId))
            .ToListAsync();

        foreach (var video in existingVideos)
        {
            if (!incomingLookup.TryGetValue(video.YouTubeVideoId, out var incoming)) continue;
            if (incoming.ThumbnailUrl != null)
                video.ThumbnailUrl = incoming.ThumbnailUrl;
            video.IsShort = incoming.IsShort;
            if (video.Status == VideoStatus.Unwatched && alreadyWatchedIds.Contains(video.YouTubeVideoId))
                video.Status = VideoStatus.Watched;
        }

        await db.SaveChangesAsync();
    }

    public async Task<HashSet<string>> GetAllWatchHistoryIdsAsync()
    {
        await using var db = await factory.CreateDbContextAsync();
        return await db.WatchHistory.Select(w => w.YouTubeVideoId).ToHashSetAsync();
    }

    public async Task SaveWatchHistoryAsync(IEnumerable<string> youTubeVideoIds)
    {
        await using var db = await factory.CreateDbContextAsync();

        var incoming = youTubeVideoIds.Distinct().ToList();
        var existing = await db.WatchHistory
            .Where(w => incoming.Contains(w.YouTubeVideoId))
            .Select(w => w.YouTubeVideoId)
            .ToHashSetAsync();

        var newEntries = incoming
            .Where(id => !existing.Contains(id))
            .Select(id => new WatchHistoryEntry { YouTubeVideoId = id })
            .ToList();

        if (newEntries.Count > 0)
        {
            db.WatchHistory.AddRange(newEntries);
            await db.SaveChangesAsync();
        }
    }

    public async Task<int> MarkWatchedByYouTubeIdsAsync(IEnumerable<string> youTubeVideoIds)
    {
        await using var db = await factory.CreateDbContextAsync();
        var idList = youTubeVideoIds.ToList();
        return await db.Videos
            .Where(v => idList.Contains(v.YouTubeVideoId) && v.Status == VideoStatus.Unwatched)
            .ExecuteUpdateAsync(s => s.SetProperty(v => v.Status, VideoStatus.Watched));
    }

    public async Task MoveChannelBetweenListsAsync(int channelId, int fromListId, int toListId)
    {
        await using var db = await factory.CreateDbContextAsync();

        var fromEntry = await db.Set<ChannelListChannel>()
            .FirstOrDefaultAsync(j => j.ListsId == fromListId && j.ChannelsId == channelId);
        if (fromEntry != null)
            db.Set<ChannelListChannel>().Remove(fromEntry);

        var alreadyInTarget = await db.Set<ChannelListChannel>()
            .AnyAsync(j => j.ListsId == toListId && j.ChannelsId == channelId);

        if (!alreadyInTarget)
        {
            var maxOrder = await db.Set<ChannelListChannel>()
                .Where(j => j.ListsId == toListId)
                .Select(j => (int?)j.SortOrder)
                .MaxAsync() ?? -1;

            db.Set<ChannelListChannel>().Add(new ChannelListChannel
            {
                ChannelsId = channelId,
                ListsId = toListId,
                SortOrder = maxOrder + 1
            });
        }

        await db.SaveChangesAsync();
    }

    public async Task UpdateChannelOrderAsync(int listId, IEnumerable<int> channelIds)
    {
        await using var db = await factory.CreateDbContextAsync();
        var ids = channelIds.ToList();
        var entries = await db.Set<ChannelListChannel>()
            .Where(j => j.ListsId == listId)
            .ToListAsync();

        for (int i = 0; i < ids.Count; i++)
        {
            var entry = entries.FirstOrDefault(e => e.ChannelsId == ids[i]);
            if (entry != null)
                entry.SortOrder = i;
        }
        await db.SaveChangesAsync();
    }

    public async Task UpdateVideoStarredAsync(int videoId, bool isStarred)
    {
        await using var db = await factory.CreateDbContextAsync();
        await db.Videos
            .Where(v => v.Id == videoId)
            .ExecuteUpdateAsync(s => s.SetProperty(v => v.IsStarred, isStarred));
    }

    public async Task UpdateChannelLastFetchedAsync(int channelId)
    {
        await using var db = await factory.CreateDbContextAsync();
        await db.Channels
            .Where(c => c.Id == channelId)
            .ExecuteUpdateAsync(s => s.SetProperty(c => c.LastFetchedAt, DateTime.UtcNow));
    }
}
