using System.IO;
using Google.Apis.Auth.OAuth2;
using Google.Apis.Services;
using GoogleYT = Google.Apis.YouTube.v3;

namespace YouTubeTool.Services;

public record ChannelInfo(string YouTubeChannelId, string Name, string? ThumbnailUrl);
public record VideoInfo(string YouTubeVideoId, string Title, string? ThumbnailUrl, DateTime PublishedAt, bool IsShort = false, bool IsMembersOnly = false);

// Thrown when an InnerTube request comes back as logged-out — the session cookies are present
// but no longer authenticate. Callers can catch this specifically to prompt a fresh sign-in.
public class YouTubeSessionExpiredException()
    : Exception("Your YouTube session has expired. Sign in again to continue.");

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

    // Members-only videos are invisible to the Data API: they never appear in a channel's public
    // uploads playlist (UU...), and an API key isn't authenticated as anyone, let alone as a member.
    // Reaching them means going through InnerTube with the WebView2 session, like Sync Watch History.
    //
    // They are NOT in a separate system playlist. A "UUMO"/"UUMF" members-only playlist is a
    // plausible-sounding guess that does not exist: YouTube answers HTTP 200 with
    // alerts[].alertRenderer "The playlist does not exist." They are listed inline with the ordinary
    // videos on the channel's **Videos tab**, which is what this reads.
    //
    // TWO signals are needed, because the badge alone misses the case this feature exists for.
    //
    //   1. An explicit badge (BADGE_MEMBERS_ONLY / "Members only") on the entry. Reliable, but
    //      observed only on channels the viewer is NOT a member of — it's the join prompt.
    //   2. Present in the authenticated tab but ABSENT from the public uploads playlist. This is
    //      what catches members-only *early access*: the creator posts a video to members first and
    //      releases it publicly a day or so later. During that window it is visible to a member and
    //      missing from the uploads playlist the API key sees — which is exactly the case that
    //      prompted this feature (an Ambition Strikes preview, public again ~2 hours later).
    //
    // Signal 2 needs two guards, or it mislabels a channel's back catalogue:
    //
    //   * A date floor. MaxVideosPerChannel caps the public fetch (400 here) while the tab walk goes
    //     deeper, so anything older than the oldest video the public fetch returned is simply out of
    //     range, not members-only. Within [oldestPublicDate, now] the public fetch IS exhaustive for
    //     public videos — newest-first pagination guarantees no gaps — so absence in that window is
    //     meaningful. Without this guard, 23 and 79 five-year-old public videos got flagged on two
    //     test channels.
    //   * Skip premieres and live broadcasts. They appear in the tab before entering the uploads
    //     playlist, so they'd look identical to early access.
    //
    // Signal 2 is skipped entirely when the public fetch returned nothing, since "absent from an
    // empty set" would flag every video on the channel.
    //
    // Once the creator makes the video public, the ordinary refresh re-upserts it with
    // IsMembersOnly=false, so the flag clears itself without special handling.
    //
    // InnerTube gives IDs, titles and thumbnails but only a relative date ("3 weeks ago"), useless
    // for the oldest-first ordering the app is built around. So results are enriched through the
    // Data API's videos.list (1 unit per 50) for the real publishedAt and the duration for Shorts
    // detection — that works with a plain API key because members-only videos are publicly *listed*,
    // just not publicly *playable*. Anything videos.list won't return falls back to the InnerTube
    // title/thumbnail and a date approximated from the relative text.
    public async Task<List<VideoInfo>> FetchMembersOnlyVideosAsync(
        string ytChannelId,
        Dictionary<string, string> cookies,
        string apiKey,
        IReadOnlyCollection<string> publicVideoIds,
        DateTime? oldestPublicDate,
        IProgress<string>? progress = null,
        string? onBehalfOfUser = null,
        int maxVideos = 50)
    {
        if (!ytChannelId.StartsWith("UC", StringComparison.Ordinal))
            return [];

        if (!cookies.TryGetValue("SAPISID", out var sapisid))
            throw new Exception("YouTube session not found. Sign in to YouTube to fetch members-only videos.");

        using var http = new System.Net.Http.HttpClient();
        http.DefaultRequestHeaders.Add("Cookie", string.Join("; ", cookies.Select(kv => $"{kv.Key}={kv.Value}")));
        http.DefaultRequestHeaders.Add("Authorization", ChromeCookieService.BuildSapiSidHash(sapisid));
        http.DefaultRequestHeaders.Add("X-Origin", "https://www.youtube.com");
        http.DefaultRequestHeaders.Add("Origin", "https://www.youtube.com");
        http.DefaultRequestHeaders.Add("User-Agent",
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36");

        var userContext = onBehalfOfUser != null
            ? $$$"""{"onBehalfOfUser":"{{{onBehalfOfUser}}}"}"""
            : "{}";
        var context = $$$"""{"client":{"clientName":"WEB","clientVersion":"2.20240101.00.00","hl":"en","gl":"US"},"user":{{{userContext}}}}""";

        var logDir = Path.Combine(Path.GetTempPath(), "YouTubeToolLogs");
        try { Directory.CreateDirectory(logDir); } catch { }

        // A channel splits its uploads across separate tabs, and members-only content can sit in any
        // of them — measured on one channel: 3 markers in Videos and 15 in Shorts. Reading only the
        // Videos tab silently missed all of the latter. These params values are YouTube's fixed
        // per-tab identifiers.
        var tabs = new (string Name, string Params)[]
        {
            ("videos",  "EgZ2aWRlb3PyBgQKAjoA"),
            ("shorts",  "EgZzaG9ydHPyBgUKA5oBAA%3D%3D"),
            ("streams", "EgdzdHJlYW1z8gYECgJ6AA%3D%3D"),
        };

        var publicIds = publicVideoIds as HashSet<string>
            ?? new HashSet<string>(publicVideoIds, StringComparer.Ordinal);
        var useDiff = publicIds.Count > 0;

        var raw = new List<RawMemberVideo>();
        var seenAcrossTabs = new HashSet<string>(StringComparer.Ordinal);
        foreach (var tab in tabs)
        {
            var body = $$"""{"browseId":"{{ytChannelId}}","params":"{{tab.Params}}","context":{{context}}}""";
            var fromTab = await RunMemberStrategyAsync(
                http, context, logDir, ytChannelId, tab.Name, body, publicIds, useDiff, maxVideos, progress);

            foreach (var v in fromTab)
                if (seenAcrossTabs.Add(v.Id))
                    raw.Add(v);
        }

        if (raw.Count == 0) return [];

        var trimmed = raw.Take(maxVideos).ToList();

        // Enrich with real publish dates + durations. If the API key is missing or the call fails,
        // fall back to InnerTube data rather than losing the videos entirely.
        Dictionary<string, VideoDetails> details = [];
        if (!string.IsNullOrWhiteSpace(apiKey))
        {
            try
            {
                using var svc = BuildService(apiKey);
                details = await FetchVideoDetailsAsync(svc, trimmed.Select(v => v.Id));
            }
            catch { /* fall back to InnerTube data below */ }
        }

        var result = new List<VideoInfo>();
        int droppedOld = 0, droppedUpcoming = 0;

        foreach (var v in trimmed)
        {
            details.TryGetValue(v.Id, out var d);

            // A badge is proof on its own. A diff hit is only meaningful inside the window the
            // public fetch actually covered, and only for something already published.
            if (!v.Badged)
            {
                var published = d?.Published ?? v.ApproxPublished;
                if (oldestPublicDate.HasValue && published < oldestPublicDate.Value)
                {
                    droppedOld++;
                    continue;
                }
                if (d != null && !string.Equals(d.LiveState, "none", StringComparison.OrdinalIgnoreCase))
                {
                    droppedUpcoming++;
                    continue;
                }
            }

            result.Add(d != null
                ? new VideoInfo(
                    v.Id,
                    d.Title,
                    d.IsShort ? $"https://i.ytimg.com/vi/{v.Id}/oar2.jpg" : d.Thumb ?? v.ThumbnailUrl,
                    d.Published,
                    d.IsShort,
                    IsMembersOnly: true)
                : new VideoInfo(v.Id, v.Title, v.ThumbnailUrl, v.ApproxPublished, IsShort: false, IsMembersOnly: true));
        }

        LogMemberAttempt(logDir, ytChannelId, "result", 0,
            $"{result.Count} kept ({result.Count(r => trimmed.First(t => t.Id == r.YouTubeVideoId).Badged)} badged), " +
            $"dropped {droppedOld} older than public fetch, {droppedUpcoming} upcoming/live");
        return result;
    }

    private sealed record VideoDetails(string Title, string? Thumb, DateTime Published, bool IsShort, string LiveState);

    // Badged = carried an explicit members-only badge (proof on its own). Otherwise it was found by
    // absence from the public uploads set, which only holds inside the fetched date window.
    private record RawMemberVideo(string Id, string Title, string? ThumbnailUrl, DateTime ApproxPublished, bool Badged);

    private static async Task<Dictionary<string, VideoDetails>> FetchVideoDetailsAsync(
        GoogleYT.YouTubeService svc, IEnumerable<string> videoIds)
    {
        var result = new Dictionary<string, VideoDetails>(StringComparer.Ordinal);
        var idList = videoIds.ToList();

        for (int i = 0; i < idList.Count; i += 50)
        {
            var req = svc.Videos.List("snippet,contentDetails");
            req.Id = string.Join(",", idList.Skip(i).Take(50));
            req.MaxResults = 50;
            var resp = await req.ExecuteAsync();

            foreach (var video in resp.Items ?? [])
            {
                if (video.Id == null) continue;

                bool isShort = false;
                var duration = video.ContentDetails?.Duration;
                if (duration != null)
                {
                    try { isShort = System.Xml.XmlConvert.ToTimeSpan(duration).TotalSeconds <= 180; }
                    catch { /* skip unparseable durations */ }
                }

                result[video.Id] = new VideoDetails(
                    video.Snippet?.Title ?? "(no title)",
                    video.Snippet?.Thumbnails?.Medium?.Url ?? video.Snippet?.Thumbnails?.Default__?.Url,
                    video.Snippet?.PublishedAtDateTimeOffset?.UtcDateTime ?? DateTime.UtcNow,
                    isShort,
                    video.Snippet?.LiveBroadcastContent ?? "none");
            }
        }

        return result;
    }

    // Walks one channel tab, following continuations, collecting badged members-only entries.
    // Returns empty rather than throwing when that tab has none (or doesn't exist).
    private static async Task<List<RawMemberVideo>> RunMemberStrategyAsync(
        System.Net.Http.HttpClient http,
        string context,
        string logDir,
        string ytChannelId,
        string tabName,
        string initialBody,
        HashSet<string> publicIds,
        bool useDiff,
        int maxVideos,
        IProgress<string>? progress)
    {
        var found = new List<RawMemberVideo>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        // Paging is driven by how many videos we've *scanned*, not how many members-only ones we
        // found: a page of entirely public videos is normal and must not stop the walk. Scanning to
        // maxVideos keeps this pass the same depth as the regular uploads fetch.
        var scanned = new HashSet<string>(StringComparer.Ordinal);
        string? continuation = null;
        int page = 0;

        while (scanned.Count < maxVideos && page < 20)
        {
            progress?.Report($"Scanning {tabName} for members-only... ({scanned.Count} checked, {found.Count} found)");

            var bodyJson = continuation == null
                ? initialBody
                : $$"""{"continuation":"{{continuation}}","context":{{context}}}""";

            using var httpContent = new System.Net.Http.StringContent(
                bodyJson, System.Text.Encoding.UTF8, "application/json");
            var resp = await http.PostAsync("https://www.youtube.com/youtubei/v1/browse", httpContent);
            var json = await resp.Content.ReadAsStringAsync();

            // Only page 0 is kept: these responses run to megabytes each, and a dozen of them per
            // channel per refresh is a lot of disk churn for diagnostics we rarely need past the
            // first page. The per-page summary line below covers the rest.
            if (page == 0)
                try { File.WriteAllText(Path.Combine(logDir, $"yt_members_{ytChannelId}_{tabName}_p0.json"), json); } catch { }

            if (!resp.IsSuccessStatusCode)
            {
                LogMemberAttempt(logDir, ytChannelId, tabName, page, $"HTTP {(int)resp.StatusCode}");
                return [];
            }

            using var doc = System.Text.Json.JsonDocument.Parse(json);

            if (page == 0 && IsLoggedOut(doc.RootElement))
                throw new YouTubeSessionExpiredException();

            // Errors arrive as HTTP 200 with an alerts[] array, not a 404 — this is the check that
            // actually catches "this channel has no such tab".
            var alert = GetAlertError(doc.RootElement);
            if (alert != null)
            {
                LogMemberAttempt(logDir, ytChannelId, tabName, page, $"alert: {alert}");
                return [];
            }

            var scannedBefore = scanned.Count;
            CollectVideos(doc.RootElement, found, seen, scanned, publicIds, useDiff);
            var newlyScanned = scanned.Count - scannedBefore;

            continuation = FindContinuationToken(doc.RootElement);
            LogMemberAttempt(logDir, ytChannelId, tabName, page,
                $"scanned {newlyScanned} video(s), {found.Count} candidate(s) so far, " +
                $"continuation={(continuation != null ? "yes" : "no")}");

            page++;
            if (continuation == null || newlyScanned == 0) break;
        }

        return found;
    }

    private static void LogMemberAttempt(string logDir, string ytChannelId, string strategy, int page, string outcome)
    {
        try
        {
            File.AppendAllText(Path.Combine(logDir, "yt_members_summary.txt"),
                $"[{DateTime.Now:HH:mm:ss}] {ytChannelId} {strategy} p{page}: {outcome}\n");
        }
        catch { }
    }

    // "The playlist does not exist." and friends arrive as a 200 with an alerts[] array.
    private static string? GetAlertError(System.Text.Json.JsonElement root)
    {
        if (!root.TryGetProperty("alerts", out var alerts) ||
            alerts.ValueKind != System.Text.Json.JsonValueKind.Array) return null;

        foreach (var a in alerts.EnumerateArray())
        {
            if (!a.TryGetProperty("alertRenderer", out var ar)) continue;
            if (!ar.TryGetProperty("type", out var t) ||
                !string.Equals(t.GetString(), "ERROR", StringComparison.OrdinalIgnoreCase)) continue;
            if (ar.TryGetProperty("text", out var txt)) return ReadText(txt) ?? "unknown error";
        }
        return null;
    }

    // A channel's Videos tab returns entries as videoRenderer, gridVideoRenderer or the newer
    // lockupViewModel depending on how far YouTube's rollout has reached, and the surrounding
    // wrappers change too. Walking the whole response for anything video-shaped is far more durable
    // than hard-coding paths, and costs nothing at these response sizes.
    private static void CollectVideos(
        System.Text.Json.JsonElement el,
        List<RawMemberVideo> into,
        HashSet<string> seen,
        HashSet<string> scanned,
        HashSet<string> publicIds,
        bool useDiff)
    {
        if (scanned.Count >= 2000) return; // safety valve against a pathological response

        if (el.ValueKind == System.Text.Json.JsonValueKind.Array)
        {
            foreach (var child in el.EnumerateArray())
                CollectVideos(child, into, seen, scanned, publicIds, useDiff);
            return;
        }

        if (el.ValueKind != System.Text.Json.JsonValueKind.Object) return;

        foreach (var name in new[] { "playlistVideoRenderer", "videoRenderer", "gridVideoRenderer", "lockupViewModel" })
        {
            if (!el.TryGetProperty(name, out var renderer)) continue;
            TryAddVideo(renderer, into, seen, scanned, publicIds, useDiff);
            return; // a video renderer holds no nested videos
        }

        foreach (var prop in el.EnumerateObject())
            CollectVideos(prop.Value, into, seen, scanned, publicIds, useDiff);
    }

    private static void TryAddVideo(
        System.Text.Json.JsonElement r,
        List<RawMemberVideo> into,
        HashSet<string> seen,
        HashSet<string> scanned,
        HashSet<string> publicIds,
        bool useDiff)
    {
        // videoRenderer family uses videoId; lockupViewModel uses contentId.
        string? id = null;
        if (r.TryGetProperty("videoId", out var idEl)) id = idEl.GetString();
        else if (r.TryGetProperty("contentId", out var contentId)) id = contentId.GetString();

        if (string.IsNullOrEmpty(id)) return;
        scanned.Add(id);

        // Signal 1 — an explicit badge. Shapes vary a lot (metadataBadgeRenderer.style, badges[],
        // thumbnailOverlay, badgeViewModel...), so match the marker anywhere in this one video's
        // subtree rather than chasing each shape. Scoped to one entry, so it can't leak across videos.
        var rawText = r.GetRawText();
        var badged = rawText.Contains("MEMBERS_ONLY", StringComparison.Ordinal)
                  || rawText.Contains("Members only", StringComparison.OrdinalIgnoreCase);

        // Signal 2 — visible here but absent from the public uploads playlist. Catches members-only
        // early access. Date/premiere guards are applied after enrichment, where real dates exist.
        var missingFromPublic = useDiff && !publicIds.Contains(id);

        if (!badged && !missingFromPublic) return;
        if (!seen.Add(id)) return;

        var title = id;
        if (r.TryGetProperty("title", out var titleEl))
            title = ReadText(titleEl) ?? id;

        string? thumb = null;
        if (r.TryGetProperty("thumbnail", out var thumbEl) &&
            thumbEl.TryGetProperty("thumbnails", out var thumbs) &&
            thumbs.ValueKind == System.Text.Json.JsonValueKind.Array)
        {
            var arr = thumbs.EnumerateArray().ToArray();
            if (arr.Length > 0 && arr[^1].TryGetProperty("url", out var urlEl))
                thumb = urlEl.GetString();
        }
        // lockupViewModel buries its image; the enrichment pass supplies the real one anyway.
        thumb ??= $"https://i.ytimg.com/vi/{id}/mqdefault.jpg";

        into.Add(new RawMemberVideo(id, title, thumb, ParseRelativeDate(r), badged));
    }

    // InnerTube text nodes are {"simpleText":...}, {"runs":[{"text":...}]}, or — on the newer
    // viewModel renderers — {"content":...}.
    private static string? ReadText(System.Text.Json.JsonElement el)
    {
        if (el.ValueKind == System.Text.Json.JsonValueKind.String) return el.GetString();
        if (el.ValueKind != System.Text.Json.JsonValueKind.Object) return null;
        if (el.TryGetProperty("simpleText", out var st)) return st.GetString();
        if (el.TryGetProperty("content", out var c) && c.ValueKind == System.Text.Json.JsonValueKind.String)
            return c.GetString();
        if (el.TryGetProperty("runs", out var runs) && runs.ValueKind == System.Text.Json.JsonValueKind.Array)
            return string.Concat(runs.EnumerateArray()
                .Select(r => r.TryGetProperty("text", out var t) ? t.GetString() : null));
        return null;
    }

    // Continuation tokens sit at different depths per route, so search rather than navigate.
    private static string? FindContinuationToken(System.Text.Json.JsonElement el)
    {
        if (el.ValueKind == System.Text.Json.JsonValueKind.Array)
        {
            foreach (var child in el.EnumerateArray())
            {
                var t = FindContinuationToken(child);
                if (t != null) return t;
            }
            return null;
        }

        if (el.ValueKind != System.Text.Json.JsonValueKind.Object) return null;

        if (el.TryGetProperty("continuationCommand", out var cmd) &&
            cmd.TryGetProperty("token", out var token))
        {
            var value = token.GetString();
            if (!string.IsNullOrEmpty(value)) return value;
        }

        foreach (var prop in el.EnumerateObject())
        {
            var t = FindContinuationToken(prop.Value);
            if (t != null) return t;
        }
        return null;
    }

    // Renderers carry only a relative age ("Streamed 3 weeks ago"), under videoInfo on playlist
    // entries and publishedTimeText on channel/grid entries. Used purely as a fallback ordering
    // key when videos.list didn't return the video.
    private static DateTime ParseRelativeDate(System.Text.Json.JsonElement r)
    {
        try
        {
            foreach (var field in new[] { "publishedTimeText", "videoInfo" })
            {
                if (!r.TryGetProperty(field, out var el)) continue;
                var parsed = ParseRelativeText(ReadText(el));
                if (parsed != null) return parsed.Value;
            }
        }
        catch { }
        return DateTime.UtcNow;
    }

    private static DateTime? ParseRelativeText(string? text)
    {
        if (string.IsNullOrEmpty(text)) return null;

        var m = System.Text.RegularExpressions.Regex.Match(
            text, @"(\d+)\s+(second|minute|hour|day|week|month|year)s?\s+ago",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (!m.Success) return null;

        var n = int.Parse(m.Groups[1].Value);
        return m.Groups[2].Value.ToLowerInvariant() switch
        {
            "second" => DateTime.UtcNow.AddSeconds(-n),
            "minute" => DateTime.UtcNow.AddMinutes(-n),
            "hour" => DateTime.UtcNow.AddHours(-n),
            "day" => DateTime.UtcNow.AddDays(-n),
            "week" => DateTime.UtcNow.AddDays(-7 * n),
            "month" => DateTime.UtcNow.AddMonths(-n),
            _ => DateTime.UtcNow.AddYears(-n),
        };
    }

    // Fetch unique subscribed channels via YouTube's InnerTube API using browser session cookies.
    public async Task<List<ChannelInfo>> FetchSubscribedChannelsViaInnerTubeAsync(
        Dictionary<string, string> cookies,
        IProgress<string>? progress = null,
        string? onBehalfOfUser = null)
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

        // Include onBehalfOfUser when acting as a brand account (secondary channel)
        var userContext = onBehalfOfUser != null
            ? $$$"""{"onBehalfOfUser":"{{{onBehalfOfUser}}}"}"""
            : "{}";
        var context = $$$"""{"client":{"clientName":"WEB","clientVersion":"2.20240101.00.00","hl":"en","gl":"US"},"user":{{{userContext}}}}""";
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

            if (pageNum == 0 && IsLoggedOut(doc.RootElement))
                throw new YouTubeSessionExpiredException();

            // Log which account is active on the first page
            if (pageNum == 0)
            {
                try
                {
                    var datasyncId = doc.RootElement
                        .GetProperty("responseContext")
                        .GetProperty("mainAppWebResponseContext")
                        .GetProperty("datasyncId")
                        .GetString() ?? "unknown";
                    File.AppendAllText(Path.Combine(logDir, "yt_subscriptions_summary.txt"),
                        $"--- Run started: datasyncId={datasyncId}, onBehalfOfUser={onBehalfOfUser ?? "(none)"} ---\n");
                }
                catch { }
            }

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
            // chips[] items are wrapped: { "chipViewModel": { "tapCommand": ... } }
            if (!chip.TryGetProperty("chipViewModel", out var chipVm)) return null;

            var listItems = chipVm
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
                    // listItems[] items are wrapped: { "listItemViewModel": { "rendererContext": ... } }
                    if (!listItem.TryGetProperty("listItemViewModel", out var listItemVm)) continue;

                    var commands = listItemVm
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
        string? onBehalfOfUser = null,
        int maxPages = 5)
    {
        if (!cookies.TryGetValue("SAPISID", out var sapisid))
            throw new Exception("YouTube session not found. Make sure you are signed in to YouTube.");

        var cookieHeader = string.Join("; ", cookies.Select(kv => $"{kv.Key}={kv.Value}"));

        using var http = new System.Net.Http.HttpClient();
        http.DefaultRequestHeaders.Add("Cookie", cookieHeader);
        http.DefaultRequestHeaders.Add("Authorization", ChromeCookieService.BuildSapiSidHash(sapisid));
        http.DefaultRequestHeaders.Add("X-Origin", "https://www.youtube.com");
        http.DefaultRequestHeaders.Add("Origin", "https://www.youtube.com");
        http.DefaultRequestHeaders.Add("User-Agent",
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36");

        var userContext = onBehalfOfUser != null
            ? $$$"""{"onBehalfOfUser":"{{{onBehalfOfUser}}}"}"""
            : "{}";
        var context = $$$"""{"client":{"clientName":"WEB","clientVersion":"2.20240101.00.00","hl":"en","gl":"US"},"user":{{{userContext}}}}""";
        var allIds = new List<string>();
        string? continuation = null;

        var logDir = Path.Combine(Path.GetTempPath(), "YouTubeToolLogs");
        try { Directory.CreateDirectory(logDir); } catch { }

        // Dump the cookie NAMES (not values) and onBehalfOfUser so we can tell, when a sync
        // comes back logged-out, whether the session cookies were even present.
        try
        {
            var diag = $"onBehalfOfUser={onBehalfOfUser ?? "(none)"}\ncookie count={cookies.Count}\nnames:\n  "
                + string.Join("\n  ", cookies.Keys.OrderBy(k => k));
            File.WriteAllText(Path.Combine(logDir, "yt_history_cookies.txt"), diag);
        }
        catch { }

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

            // Save every page response for debugging — without this we have no way to tell
            // whether a zero-result sync is a sign-in problem or a changed InnerTube structure.
            try { File.WriteAllText(Path.Combine(logDir, $"yt_history_p{page}.json"), json); } catch { }

            if (!resp.IsSuccessStatusCode)
                throw new Exception($"InnerTube returned HTTP {(int)resp.StatusCode} ({resp.ReasonPhrase}). Check that you are signed in via Settings.");

            using var doc = System.Text.Json.JsonDocument.Parse(json);

            if (page == 0 && IsLoggedOut(doc.RootElement))
                throw new YouTubeSessionExpiredException();

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

    // YouTube returns HTTP 200 with a fully-rendered "signed out" feed when the session cookies
    // are present but no longer authenticate (expired/revoked). responseContext.loggedOut is the
    // authoritative signal — checking it lets us fail with a clear, recoverable message instead of
    // silently returning an empty list.
    private static bool IsLoggedOut(System.Text.Json.JsonElement root)
    {
        return root.TryGetProperty("responseContext", out var ctx)
            && ctx.TryGetProperty("mainAppWebResponseContext", out var webCtx)
            && webCtx.TryGetProperty("loggedOut", out var loggedOut)
            && loggedOut.ValueKind == System.Text.Json.JsonValueKind.True;
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
                // Sort-chip continuations use reloadContinuationItemsCommand (replaces content)
                // Regular pagination uses appendContinuationItemsAction (adds to content)
                if (action.TryGetProperty("reloadContinuationItemsCommand", out var reloadAction))
                    return reloadAction.GetProperty("continuationItems").EnumerateArray().ToArray();
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
