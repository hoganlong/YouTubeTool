using System.IO;
using Google.Apis.Auth.OAuth2;
using Google.Apis.Services;
using GoogleYT = Google.Apis.YouTube.v3;

namespace YouTubeTool.Services;

public record ChannelInfo(string YouTubeChannelId, string Name, string? ThumbnailUrl);
public record VideoInfo(string YouTubeVideoId, string Title, string? ThumbnailUrl, DateTime PublishedAt, bool IsShort = false);

public class YouTubeService
{
    public async Task<bool> ValidateApiKeyAsync(string apiKey)
    {
        try
        {
            using var svc = BuildService(apiKey);
            var req = svc.Channels.List("id");
            req.Id = "UC_x5XG1OV2P6uZZ5FSM9Ttw"; // YouTube's own channel
            req.MaxResults = 1;
            var resp = await req.ExecuteAsync();
            return resp.Items?.Count > 0;
        }
        catch
        {
            return false;
        }
    }

    public async Task<ChannelInfo> FetchChannelInfoAsync(string channelUrlOrId, string apiKey)
    {
        using var svc = BuildService(apiKey);
        var (idType, value) = ParseChannelInput(channelUrlOrId);

        var req = svc.Channels.List("id,snippet");

        switch (idType)
        {
            case "id":
                req.Id = value;
                break;
            case "handle":
                req.ForHandle = value;
                break;
            case "username":
                req.ForUsername = value;
                break;
            default:
                throw new ArgumentException($"Cannot parse channel from: {channelUrlOrId}");
        }

        req.MaxResults = 1;
        var resp = await req.ExecuteAsync();
        var item = resp.Items?.FirstOrDefault()
            ?? throw new Exception($"Channel not found: {channelUrlOrId}");

        return new ChannelInfo(
            item.Id,
            item.Snippet.Title,
            item.Snippet.Thumbnails?.Medium?.Url ?? item.Snippet.Thumbnails?.Default__?.Url);
    }

    public async Task<List<VideoInfo>> FetchRecentVideosAsync(string ytChannelId, string apiKey, int maxResults = 50)
    {
        using var svc = BuildService(apiKey);

        // Step 1: Get the uploads playlist ID (costs 1 quota unit)
        var chanReq = svc.Channels.List("contentDetails");
        chanReq.Id = ytChannelId;
        chanReq.MaxResults = 1;
        var chanResp = await chanReq.ExecuteAsync();
        // Channel not found or terminated — return empty rather than error
        var uploadsPlaylistId = chanResp.Items?.FirstOrDefault()
            ?.ContentDetails?.RelatedPlaylists?.Uploads;
        if (string.IsNullOrEmpty(uploadsPlaylistId))
            return [];

        // Step 2: Fetch videos from playlist (costs 1 quota unit per page)
        var videos = new List<VideoInfo>();
        string? pageToken = null;

        do
        {
            var playReq = svc.PlaylistItems.List("snippet");
            playReq.PlaylistId = uploadsPlaylistId;
            playReq.MaxResults = Math.Min(maxResults - videos.Count, 50);
            if (pageToken != null) playReq.PageToken = pageToken;

            GoogleYT.Data.PlaylistItemListResponse playResp;
            try
            {
                playResp = await playReq.ExecuteAsync();
            }
            catch (Exception ex) when (ex.Message.Contains("NotFound"))
            {
                // Uploads playlist not accessible — channel may be suspended or restricted
                break;
            }

            foreach (var item in playResp.Items ?? [])
            {
                var snippet = item.Snippet;
                if (snippet?.ResourceId?.VideoId == null) continue;

                var published = snippet.PublishedAtDateTimeOffset?.UtcDateTime
                    ?? DateTime.UtcNow;

                videos.Add(new VideoInfo(
                    snippet.ResourceId.VideoId,
                    snippet.Title ?? "(no title)",
                    snippet.Thumbnails?.Medium?.Url ?? snippet.Thumbnails?.Default__?.Url,
                    published));
            }

            pageToken = playResp.NextPageToken;
        } while (pageToken != null && videos.Count < maxResults);

        // Step 3: Fetch durations to detect Shorts (≤60s). Costs 1 quota unit per 50 videos.
        var shortIds = await FetchShortVideoIdsAsync(svc, videos.Select(v => v.YouTubeVideoId));

        return videos
            .Select(v => shortIds.Contains(v.YouTubeVideoId)
                ? v with { IsShort = true, ThumbnailUrl = $"https://i.ytimg.com/vi/{v.YouTubeVideoId}/oar2.jpg" }
                : v)
            .ToList();
    }

    private static async Task<HashSet<string>> FetchShortVideoIdsAsync(
        GoogleYT.YouTubeService svc, IEnumerable<string> videoIds)
    {
        var shortIds = new HashSet<string>(StringComparer.Ordinal);
        var idList = videoIds.ToList();

        // Process in batches of 50 (API max)
        for (int i = 0; i < idList.Count; i += 50)
        {
            var batch = idList.Skip(i).Take(50).ToList();
            var req = svc.Videos.List("contentDetails");
            req.Id = string.Join(",", batch);
            req.MaxResults = 50;
            var resp = await req.ExecuteAsync();

            foreach (var video in resp.Items ?? [])
            {
                var duration = video.ContentDetails?.Duration;
                if (duration == null) continue;
                try
                {
                    var ts = System.Xml.XmlConvert.ToTimeSpan(duration);
                    if (ts.TotalSeconds <= 180)
                        shortIds.Add(video.Id);
                }
                catch { /* skip unparseable durations */ }
            }
        }

        return shortIds;
    }

    // Fetch unique subscribed channels via YouTube's InnerTube API using browser session cookies.
    public async Task<List<ChannelInfo>> FetchSubscribedChannelsViaInnerTubeAsync(
        Dictionary<string, string> cookies,
        IProgress<string>? progress = null)
    {
        if (!cookies.TryGetValue("SAPISID", out var sapisid))
            throw new Exception("YouTube session not found. Make sure you are signed in to YouTube in the browser.");

        var cookieHeader = string.Join("; ", cookies.Select(kv => $"{kv.Key}={kv.Value}"));
        using var http = new System.Net.Http.HttpClient();
        http.DefaultRequestHeaders.Add("Cookie", cookieHeader);
        http.DefaultRequestHeaders.Add("Authorization", ChromeCookieService.BuildSapiSidHash(sapisid));
        http.DefaultRequestHeaders.Add("X-Origin", "https://www.youtube.com");
        http.DefaultRequestHeaders.Add("Origin", "https://www.youtube.com");
        http.DefaultRequestHeaders.Add("User-Agent",
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36");

        const string context = """{"client":{"clientName":"WEB","clientVersion":"2.20240101.00.00","hl":"en","gl":"US"}}""";
        var seen = new Dictionary<string, ChannelInfo>(StringComparer.Ordinal);
        string? continuation = null;
        int pageNum = 0;

        var logDir = Path.Combine(Path.GetTempPath(), "YouTubeToolLogs");
        try { Directory.CreateDirectory(logDir); } catch { }

        while (true)
        {
            progress?.Report($"Fetching subscriptions... ({seen.Count} channels so far)");

            var bodyJson = continuation == null
                ? $$"""{"browseId":"FEchannels","context":{{context}}}"""
                : $$"""{"continuation":"{{continuation}}","context":{{context}}}""";

            using var httpContent = new System.Net.Http.StringContent(
                bodyJson, System.Text.Encoding.UTF8, "application/json");
            var resp = await http.PostAsync("https://www.youtube.com/youtubei/v1/browse", httpContent);
            var json = await resp.Content.ReadAsStringAsync();

            if (!resp.IsSuccessStatusCode)
                throw new Exception($"InnerTube returned HTTP {(int)resp.StatusCode} ({resp.ReasonPhrase}). Check that you are signed in.");

            // Save every page response for debugging
            try { File.WriteAllText(Path.Combine(logDir, $"yt_subscriptions_p{pageNum}.json"), json); } catch { }

            using var doc = System.Text.Json.JsonDocument.Parse(json);
            var items = pageNum == 0
                ? GetInnerTubeInitialItems(doc.RootElement)
                : GetInnerTubeContinuationItems(doc.RootElement);

            var pageChannels = ExtractSubscribedChannels(items);
            foreach (var ch in pageChannels)
                seen.TryAdd(ch.YouTubeChannelId, ch);

            continuation = ExtractSubscriptionContinuationToken(items);

            // If page 0 had no continuationItemRenderer, YouTube showed a shelf preview only.
            // Look for a sort-chip continuation token in the header — that gives the full list.
            bool usedSortChip = false;
            if (pageNum == 0 && continuation == null)
            {
                continuation = ExtractSortChipContinuationToken(doc.RootElement);
                usedSortChip = continuation != null;
            }

            // Log a summary of what was parsed on this page
            try
            {
                var note = usedSortChip ? " [using sort-chip token for full list]" : "";
                File.AppendAllText(Path.Combine(logDir, "yt_subscriptions_summary.txt"),
                    $"Page {pageNum}: {pageChannels.Count} channels parsed, continuation={(continuation != null ? "yes" : "no")}{note}, total so far={seen.Count}\n");
            }
            catch { }

            pageNum++;
            // Stop if no continuation token, or if a non-initial page returned nothing (safety valve)
            if (continuation == null || (pageNum > 1 && pageChannels.Count == 0)) break;
        }

        return [.. seen.Values];
    }

    private static List<ChannelInfo> ExtractSubscribedChannels(System.Text.Json.JsonElement[] items)
    {
        // Handles two response shapes:
        // Shape A (initial FEchannels page): itemSectionRenderer > shelfRenderer
        //         > expandedShelfContentsRenderer.items[] > channelRenderer
        // Shape B (sort-chip continuation): itemSectionRenderer > contents[] > channelRenderer directly
        var channels = new List<ChannelInfo>();
        foreach (var section in items)
        {
            if (!section.TryGetProperty("itemSectionRenderer", out var isr) ||
                !isr.TryGetProperty("contents", out var isrContents)) continue;

            foreach (var isrItem in isrContents.EnumerateArray())
            {
                // Shape A: shelf preview wrapping
                if (isrItem.TryGetProperty("shelfRenderer", out var shelf) &&
                    shelf.TryGetProperty("content", out var shelfContent) &&
                    shelfContent.TryGetProperty("expandedShelfContentsRenderer", out var expanded) &&
                    expanded.TryGetProperty("items", out var shelfItems))
                {
                    foreach (var shelfItem in shelfItems.EnumerateArray())
                    {
                        if (TryParseChannelRenderer(shelfItem, out var ch) && ch != null)
                            channels.Add(ch);
                    }
                    continue;
                }

                // Shape B: channelRenderer directly in itemSectionRenderer.contents
                if (TryParseChannelRenderer(isrItem, out var directCh) && directCh != null)
                    channels.Add(directCh);
            }
        }
        return channels;
    }

    private static bool TryParseChannelRenderer(System.Text.Json.JsonElement item, out ChannelInfo? channel)
    {
        channel = null;
        if (!item.TryGetProperty("channelRenderer", out var cr)) return false;

        if (!cr.TryGetProperty("channelId", out var idEl)) return true;
        var id = idEl.GetString();
        if (string.IsNullOrEmpty(id)) return true;

        var name = id;
        if (cr.TryGetProperty("title", out var titleEl))
        {
            if (titleEl.TryGetProperty("simpleText", out var st))
                name = st.GetString() ?? id;
            else if (titleEl.TryGetProperty("runs", out var runs) && runs.GetArrayLength() > 0 &&
                     runs[0].TryGetProperty("text", out var rt))
                name = rt.GetString() ?? id;
        }

        string? thumbnailUrl = null;
        if (cr.TryGetProperty("thumbnail", out var thumb) &&
            thumb.TryGetProperty("thumbnails", out var thumbs))
        {
            var arr = thumbs.EnumerateArray().ToArray();
            if (arr.Length > 0 && arr[^1].TryGetProperty("url", out var urlEl))
            {
                var url = urlEl.GetString();
                // Thumbnail URLs are protocol-relative (//yt3...) — make them absolute
                if (url != null && url.StartsWith("//"))
                    url = "https:" + url;
                thumbnailUrl = url;
            }
        }

        channel = new ChannelInfo(id, name, thumbnailUrl);
        return true;
    }

    // Continuation token lives at the top-level sections array as a continuationItemRenderer.
    private static string? ExtractSubscriptionContinuationToken(System.Text.Json.JsonElement[] items) =>
        ExtractInnerTubeContinuationToken(items);

    // When FEchannels returns only a shelf preview (no continuationItemRenderer),
    // extract a sort-chip continuation token from the header chipBarViewModel.
    // Using this token with the browse endpoint returns the full paginated channel list.
    private static string? ExtractSortChipContinuationToken(System.Text.Json.JsonElement root)
    {
        try
        {
            var chips = root
                .GetProperty("contents")
                .GetProperty("twoColumnBrowseResultsRenderer")
                .GetProperty("tabs")[0]
                .GetProperty("tabRenderer")
                .GetProperty("content")
                .GetProperty("sectionListRenderer")
                .GetProperty("header")
                .GetProperty("chipBarViewModel")
                .GetProperty("chips");

            foreach (var chip in chips.EnumerateArray())
            {
                var token = TryExtractChipSheetToken(chip);
                if (token != null) return token;
            }
        }
        catch { }
        return null;
    }

    // Navigate the nested sheet structure inside a chipBarViewModel chip to find a continuation token.
    private static string? TryExtractChipSheetToken(System.Text.Json.JsonElement chip)
    {
        try
        {
            var listItems = chip
                .GetProperty("tapCommand")
                .GetProperty("innertubeCommand")
                .GetProperty("showSheetCommand")
                .GetProperty("panelLoadingStrategy")
                .GetProperty("inlineContent")
                .GetProperty("sheetViewModel")
                .GetProperty("content")
                .GetProperty("listViewModel")
                .GetProperty("listItems");

            foreach (var listItem in listItems.EnumerateArray())
            {
                try
                {
                    var commands = listItem
                        .GetProperty("rendererContext")
                        .GetProperty("commandContext")
                        .GetProperty("onTap")
                        .GetProperty("innertubeCommand")
                        .GetProperty("commandExecutorCommand")
                        .GetProperty("commands");

                    foreach (var cmd in commands.EnumerateArray())
                    {
                        if (cmd.TryGetProperty("continuationCommand", out var contCmd) &&
                            contCmd.TryGetProperty("token", out var tokenEl))
                        {
                            var token = tokenEl.GetString();
                            if (!string.IsNullOrEmpty(token)) return token;
                        }
                    }
                }
                catch { }
            }
        }
        catch { }
        return null;
    }

    // Fetch recently watched video IDs via YouTube's InnerTube API using browser session cookies.
    // Pages through history newest-first, stopping once all IDs on a page are already known.
    public async Task<List<string>> FetchWatchHistoryViaInnerTubeAsync(
        Dictionary<string, string> cookies,
        IProgress<string>? progress = null,
        int maxPages = 5)
    {
        if (!cookies.TryGetValue("SAPISID", out var sapisid))
            throw new Exception("YouTube session not found in Chrome/Edge. Make sure you are logged into YouTube in your browser.");

        var cookieHeader = string.Join("; ", cookies.Select(kv => $"{kv.Key}={kv.Value}"));

        using var http = new System.Net.Http.HttpClient();
        http.DefaultRequestHeaders.Add("Cookie", cookieHeader);
        http.DefaultRequestHeaders.Add("Authorization", ChromeCookieService.BuildSapiSidHash(sapisid));
        http.DefaultRequestHeaders.Add("X-Origin", "https://www.youtube.com");
        http.DefaultRequestHeaders.Add("Origin", "https://www.youtube.com");
        http.DefaultRequestHeaders.Add("User-Agent",
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36");

        const string context = """{"client":{"clientName":"WEB","clientVersion":"2.20240101.00.00","hl":"en","gl":"US"}}""";
        var allIds = new List<string>();
        string? continuation = null;

        for (int page = 0; page < maxPages; page++)
        {
            progress?.Report($"Fetching watch history... ({allIds.Count} so far)");

            var bodyJson = continuation == null
                ? $$"""{"browseId":"FEhistory","context":{{context}}}"""
                : $$"""{"continuation":"{{continuation}}","context":{{context}}}""";

            using var httpContent = new System.Net.Http.StringContent(
                bodyJson, System.Text.Encoding.UTF8, "application/json");
            var resp = await http.PostAsync(
                "https://www.youtube.com/youtubei/v1/browse", httpContent);

            var json = await resp.Content.ReadAsStringAsync();

            if (!resp.IsSuccessStatusCode)
                throw new Exception($"InnerTube returned HTTP {(int)resp.StatusCode} ({resp.ReasonPhrase}). Check that you are signed in via Settings.");

            using var doc = System.Text.Json.JsonDocument.Parse(json);

            var items = page == 0
                ? GetInnerTubeInitialItems(doc.RootElement)
                : GetInnerTubeContinuationItems(doc.RootElement);

            var pageIds = ExtractInnerTubeVideoIds(items);
            continuation = ExtractInnerTubeContinuationToken(items);

            allIds.AddRange(pageIds);

            if (pageIds.Count == 0 || continuation == null) break;
        }

        return allIds;
    }

    private static System.Text.Json.JsonElement[] GetInnerTubeInitialItems(System.Text.Json.JsonElement root)
    {
        try
        {
            var sectionList = root
                .GetProperty("contents")
                .GetProperty("twoColumnBrowseResultsRenderer")
                .GetProperty("tabs")[0]
                .GetProperty("tabRenderer")
                .GetProperty("content")
                .GetProperty("sectionListRenderer")
                .GetProperty("contents");

            // Return ALL top-level sections — includes itemSectionRenderer groups
            // (one per date: "Today", "Yesterday", etc.) and continuationItemRenderer
            return sectionList.EnumerateArray().ToArray();
        }
        catch { }
        return [];
    }

    private static System.Text.Json.JsonElement[] GetInnerTubeContinuationItems(System.Text.Json.JsonElement root)
    {
        try
        {
            foreach (var action in root.GetProperty("onResponseReceivedActions").EnumerateArray())
            {
                if (action.TryGetProperty("appendContinuationItemsAction", out var appendAction))
                    return appendAction.GetProperty("continuationItems").EnumerateArray().ToArray();
            }
        }
        catch { }
        return [];
    }

    private static List<string> ExtractInnerTubeVideoIds(System.Text.Json.JsonElement[] items)
    {
        var ids = new List<string>();
        foreach (var item in items)
        {
            // itemSectionRenderer groups videos by date ("Today", "Yesterday", etc.)
            // Recurse into their contents to get the actual video items
            if (item.TryGetProperty("itemSectionRenderer", out var section) &&
                section.TryGetProperty("contents", out var sectionContents))
            {
                foreach (var sectionItem in sectionContents.EnumerateArray())
                    ExtractVideoId(sectionItem, ids);
                continue;
            }

            ExtractVideoId(item, ids);
        }
        return ids;
    }

    private static void ExtractVideoId(System.Text.Json.JsonElement item, List<string> ids)
    {
        // Unwrap richItemRenderer if present
        var content = item;
        if (item.TryGetProperty("richItemRenderer", out var rich) &&
            rich.TryGetProperty("content", out var richContent))
            content = richContent;

        // New structure: lockupViewModel.contentId
        if (content.TryGetProperty("lockupViewModel", out var lockup) &&
            lockup.TryGetProperty("contentId", out var contentId))
        {
            var id = contentId.GetString();
            if (!string.IsNullOrEmpty(id)) ids.Add(id);
            return;
        }
        // Legacy structure: videoRenderer.videoId
        if (content.TryGetProperty("videoRenderer", out var video) &&
            video.TryGetProperty("videoId", out var videoId))
        {
            var id = videoId.GetString();
            if (!string.IsNullOrEmpty(id)) ids.Add(id);
        }
    }

    private static string? ExtractInnerTubeContinuationToken(System.Text.Json.JsonElement[] items)
    {
        foreach (var item in items)
        {
            if (item.TryGetProperty("continuationItemRenderer", out var cont) &&
                cont.TryGetProperty("continuationEndpoint", out var endpoint) &&
                endpoint.TryGetProperty("continuationCommand", out var cmd) &&
                cmd.TryGetProperty("token", out var token))
            {
                return token.GetString();
            }
        }
        return null;
    }

    private static GoogleYT.YouTubeService BuildService(string apiKey) =>
        new(new BaseClientService.Initializer
        {
            ApiKey = apiKey,
            ApplicationName = "YouTubeTool"
        });

    private static GoogleYT.YouTubeService BuildAuthenticatedService(UserCredential credential) =>
        new(new BaseClientService.Initializer
        {
            HttpClientInitializer = credential,
            ApplicationName = "YouTubeTool"
        });

    private static (string idType, string value) ParseChannelInput(string input)
    {
        input = input.Trim();

        // Raw channel ID
        if (input.StartsWith("UC", StringComparison.Ordinal) && !input.Contains('/'))
            return ("id", input);

        // URL parsing
        if (Uri.TryCreate(input, UriKind.Absolute, out var uri))
        {
            var segments = uri.AbsolutePath.TrimEnd('/').Split('/');

            // /channel/UCxxxxxxx
            var chanIdx = Array.IndexOf(segments, "channel");
            if (chanIdx >= 0 && chanIdx + 1 < segments.Length)
                return ("id", segments[chanIdx + 1]);

            // /@handle
            var atIdx = Array.FindIndex(segments, s => s.StartsWith('@'));
            if (atIdx >= 0)
                return ("handle", segments[atIdx]);

            // /c/CustomName or /user/Username
            var cIdx = Array.IndexOf(segments, "c");
            var uIdx = Array.IndexOf(segments, "user");
            int nameIdx = cIdx >= 0 ? cIdx + 1 : uIdx >= 0 ? uIdx + 1 : -1;
            if (nameIdx >= 0 && nameIdx < segments.Length)
                return ("username", segments[nameIdx]);
        }

        // Handle @handle or raw ID
        if (input.StartsWith('@'))
            return ("handle", input);

        return ("id", input);
    }
}
