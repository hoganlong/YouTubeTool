using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Input;
using YouTubeTool.Models;
using YouTubeTool.Services;

namespace YouTubeTool.ViewModels;

public class ChannelListItem : BaseViewModel
{
    private string _name;
    private int _channelsWithUnwatched;
    private int _totalUnwatched;
    public int Id { get; }
    public string Name { get => _name; set => SetProperty(ref _name, value); }
    public int ChannelsWithUnwatched { get => _channelsWithUnwatched; set { if (SetProperty(ref _channelsWithUnwatched, value)) OnPropertyChanged(nameof(DisplayName)); } }
    public int TotalUnwatched { get => _totalUnwatched; set { if (SetProperty(ref _totalUnwatched, value)) OnPropertyChanged(nameof(DisplayName)); } }
    public string DisplayName => TotalUnwatched > 0 ? $"{Name} ({ChannelsWithUnwatched}/{TotalUnwatched})" : Name;
    public ChannelListItem(ChannelList list) { Id = list.Id; _name = list.Name; }
}

public class ChannelItem : BaseViewModel
{
    private readonly DatabaseService _db;
    // Raised after a view option is persisted so the videos pane and the unwatched counts
    // can be rebuilt — the toggles change what's visible, not just what's stored.
    private readonly Func<ChannelItem, Task> _onOptionsChanged;
    private int _unwatchedCount;
    private bool _showShorts;
    private bool _showWatched;
    private bool _showMembersOnly;

    public int Id { get; }
    public string Name { get; }
    public string YouTubeChannelId { get; }
    public VideoSortOrder SortOrder { get; set; }
    public int UnwatchedCount { get => _unwatchedCount; set => SetProperty(ref _unwatchedCount, value); }
    public string DisplayName => UnwatchedCount > 0 ? $"{Name} ({UnwatchedCount})" : Name;

    public bool ShowShorts
    {
        get => _showShorts;
        set { if (SetProperty(ref _showShorts, value)) _ = ApplyAsync(_db.UpdateChannelShowShortsAsync(Id, value)); }
    }

    public bool ShowWatched
    {
        get => _showWatched;
        set { if (SetProperty(ref _showWatched, value)) _ = ApplyAsync(_db.UpdateChannelShowWatchedAsync(Id, value)); }
    }

    public bool ShowMembersOnly
    {
        get => _showMembersOnly;
        set { if (SetProperty(ref _showMembersOnly, value)) _ = ApplyAsync(_db.UpdateChannelShowMembersOnlyAsync(Id, value)); }
    }

    public ChannelItem(Channel channel, DatabaseService db, Func<ChannelItem, Task> onOptionsChanged)
    {
        Id = channel.Id;
        Name = channel.Name;
        YouTubeChannelId = channel.YouTubeChannelId;
        SortOrder = channel.VideoSortOrder;
        _showShorts = channel.ShowShorts;
        _showWatched = channel.ShowWatched;
        _showMembersOnly = channel.ShowMembersOnly;
        _db = db;
        _onOptionsChanged = onOptionsChanged;
    }

    private async Task ApplyAsync(Task save)
    {
        await save;
        await _onOptionsChanged(this);
    }

    public void RefreshDisplayName() => OnPropertyChanged(nameof(DisplayName));
}

public enum RefreshMode { All, Top1, Top2, Top3, FirstHalf, SecondHalf }

public class MainViewModel : BaseViewModel
{
    private readonly DatabaseService _db;
    private readonly YouTubeService _yt;
    private readonly SettingsService _settings;
    private readonly GoogleAuthService _auth;
    private readonly TakeoutImportService _takeout;
    private readonly ChromeCookieService _cookies;
    private readonly WebView2CookieService _webView2Cookies;

    private ChannelListItem? _selectedList;
    private ChannelItem? _selectedChannel;
    private bool _isBusy;
    private string _statusMessage = "Ready";
    private string _addChannelText = string.Empty;
    private double _uiScale = 1.0;
    private readonly List<string> _messageHistory = [];
    private RefreshMode _refreshMode = RefreshMode.All;
    private bool _suppressVideoLoad;

    public ObservableCollection<ChannelListItem> Lists { get; } = [];
    public ObservableCollection<ChannelItem> Channels { get; } = [];
    public ObservableCollection<VideoViewModel> Videos { get; } = [];

    public ChannelListItem? SelectedList
    {
        get => _selectedList;
        set
        {
            if (SetProperty(ref _selectedList, value))
                _ = LoadChannelsAsync();
        }
    }

    public ChannelItem? SelectedChannel
    {
        get => _selectedChannel;
        set
        {
            if (SetProperty(ref _selectedChannel, value))
            {
                OnPropertyChanged(nameof(ChannelSortOrder));
                OnPropertyChanged(nameof(IsChannelSelected));
                if (!_suppressVideoLoad)
                    _ = LoadVideosAsync();
            }
        }
    }

    public bool IsChannelSelected => _selectedChannel != null;

    public VideoSortOrder ChannelSortOrder
    {
        get => _selectedChannel?.SortOrder ?? VideoSortOrder.OldestFirst;
        set
        {
            if (_selectedChannel == null || _selectedChannel.SortOrder == value) return;
            _selectedChannel.SortOrder = value;
            OnPropertyChanged();
            _ = _db.UpdateChannelSortOrderAsync(_selectedChannel.Id, value);
            _ = LoadVideosAsync();
        }
    }

    public bool IsBusy { get => _isBusy; set => SetProperty(ref _isBusy, value); }
    public bool HasNoVideos => Videos.Count == 0 && SelectedList != null;

    // Called by ChannelItem after one of its right-click view options is saved. Only the
    // selected channel's videos are on screen, so a reload is needed only for that one —
    // but the counts shift for whichever channel changed.
    private async Task OnChannelOptionsChangedAsync(ChannelItem channel)
    {
        if (ReferenceEquals(channel, SelectedChannel))
            await LoadVideosAsync();
        await RefreshChannelCountsAsync();
    }

    public string StatusMessage
    {
        get => _statusMessage;
        set
        {
            if (SetProperty(ref _statusMessage, value) && !string.IsNullOrEmpty(value))
            {
                _messageHistory.Add($"[{DateTime.Now:HH:mm:ss}] {value}");
                if (_messageHistory.Count > 100)
                    _messageHistory.RemoveAt(0);
            }
        }
    }
    public string AddChannelText { get => _addChannelText; set => SetProperty(ref _addChannelText, value); }
    public double UiScale { get => _uiScale; set => SetProperty(ref _uiScale, value); }

    public RefreshMode RefreshMode
    {
        get => _refreshMode;
        set
        {
            if (!SetProperty(ref _refreshMode, value)) return;
            OnPropertyChanged(nameof(RefreshAllButtonText));
            var s = _settings.LoadSettings();
            s.RefreshMode = value.ToString();
            _settings.SaveSettings(s);
        }
    }

    public string RefreshAllButtonText => _refreshMode switch
    {
        RefreshMode.Top1 => "↻ Refresh Top 1",
        RefreshMode.Top2 => "↻ Refresh Top 2",
        RefreshMode.Top3 => "↻ Refresh Top 3",
        RefreshMode.FirstHalf => "↻ Refresh First Half",
        RefreshMode.SecondHalf => "↻ Refresh Second Half",
        _ => "↻ Refresh All",
    };

    public void SetRefreshMode(string tag)
    {
        if (Enum.TryParse<RefreshMode>(tag, ignoreCase: true, out var m))
            RefreshMode = m;
    }

    public ICommand AddListCommand { get; }
    public ICommand DeleteListCommand { get; }
    public ICommand AddChannelCommand { get; }
    public ICommand RemoveChannelCommand { get; }
    public ICommand RefreshCommand { get; }
    public ICommand MarkAllWatchedCommand { get; }
    public ICommand SyncWatchHistoryCommand { get; }
    public ICommand ImportTakeoutCommand { get; }
    public ICommand ExportListCommand { get; }
    public ICommand ImportListCommand { get; }
    public ICommand OpenSettingsCommand { get; }
    public ICommand ShowMessageHistoryCommand { get; }
    public ICommand RefreshAllCommand { get; }
    public ICommand LoadFromSubscriptionsCommand { get; }

    public MainViewModel(DatabaseService db, YouTubeService yt, SettingsService settings, GoogleAuthService auth, TakeoutImportService takeout, ChromeCookieService cookies, WebView2CookieService webView2Cookies)
    {
        _db = db;
        _yt = yt;
        _settings = settings;
        _auth = auth;
        _takeout = takeout;
        _cookies = cookies;
        _webView2Cookies = webView2Cookies;

        var loadedSettings = _settings.LoadSettings();
        _uiScale = loadedSettings.UiScale;
        if (Enum.TryParse<RefreshMode>(loadedSettings.RefreshMode, ignoreCase: true, out var savedMode))
            _refreshMode = savedMode;

        AddListCommand = new AsyncRelayCommand(AddListAsync);
        DeleteListCommand = new AsyncRelayCommand(DeleteListAsync, () => SelectedList != null);
        AddChannelCommand = new AsyncRelayCommand(AddChannelAsync, () => SelectedList != null && !string.IsNullOrWhiteSpace(AddChannelText));
        RemoveChannelCommand = new AsyncRelayCommand(RemoveChannelAsync, () => SelectedList != null && SelectedChannel != null);
        RefreshCommand = new AsyncRelayCommand(RefreshAsync, () => SelectedList != null && !IsBusy);
        MarkAllWatchedCommand = new AsyncRelayCommand(MarkAllWatchedAsync, () => SelectedList != null && Videos.Any(v => v.Status == VideoStatus.Unwatched));
        SyncWatchHistoryCommand = new AsyncRelayCommand(SyncWatchHistoryAsync, () => !IsBusy);
        ImportTakeoutCommand = new AsyncRelayCommand(ImportTakeoutAsync, () => !IsBusy);
        ExportListCommand = new AsyncRelayCommand(ExportListAsync, () => SelectedList != null && !IsBusy);
        ImportListCommand = new AsyncRelayCommand(ImportListAsync, () => !IsBusy);
        OpenSettingsCommand = new RelayCommand(OpenSettings);
        ShowMessageHistoryCommand = new RelayCommand(ShowMessageHistory);
        RefreshAllCommand = new AsyncRelayCommand(RefreshAllAsync, () => !IsBusy);
        LoadFromSubscriptionsCommand = new AsyncRelayCommand(LoadFromSubscriptionsAsync, () => !IsBusy);
    }

    public async Task InitializeAsync()
    {
        var lists = await _db.GetAllListsAsync();
        Lists.Clear();
        foreach (var l in lists)
            Lists.Add(new ChannelListItem(l));
        await RefreshAllListCountsAsync();
    }

    private async Task LoadChannelsAsync()
    {
        Channels.Clear();
        Videos.Clear();
        if (SelectedList == null) return;

        IsBusy = true;
        StatusMessage = $"Loading channels for \"{SelectedList.Name}\"...";

        var channels = await _db.GetChannelsForListAsync(SelectedList.Id);
        foreach (var c in channels)
            Channels.Add(new ChannelItem(c, _db, OnChannelOptionsChangedAsync));

        StatusMessage = "Counting unwatched videos...";
        await RefreshChannelCountsAsync();
        SelectedChannel = Channels.FirstOrDefault();
    }

    private async Task RefreshChannelCountsAsync()
    {
        if (SelectedList == null) return;
        var counts = await _db.GetUnwatchedCountsForListAsync(SelectedList.Id);
        foreach (var ch in Channels)
        {
            ch.UnwatchedCount = counts.TryGetValue(ch.Id, out var n) ? n : 0;
            ch.RefreshDisplayName();
        }
        await RefreshAllListCountsAsync();
    }

    private async Task RefreshAllListCountsAsync()
    {
        var summary = await _db.GetUnwatchedSummaryForAllListsAsync();
        foreach (var list in Lists)
        {
            if (summary.TryGetValue(list.Id, out var s))
            {
                list.ChannelsWithUnwatched = s.ChannelsWithUnwatched;
                list.TotalUnwatched = s.TotalUnwatched;
            }
            else
            {
                list.ChannelsWithUnwatched = 0;
                list.TotalUnwatched = 0;
            }
        }
    }

    private async Task LoadVideosAsync()
    {
        Videos.Clear();
        if (SelectedList == null)
        {
            IsBusy = false;
            StatusMessage = "Ready";
            return;
        }

        var context = SelectedChannel != null ? $"\"{SelectedChannel.Name}\"" : $"\"{SelectedList.Name}\"";
        StatusMessage = $"Loading videos for {context}...";

        // Show Watched is a per-channel option now, so the whole-list view (no channel selected)
        // has no toggle to read and always shows unwatched only.
        var sortOrder = SelectedChannel?.SortOrder ?? VideoSortOrder.OldestFirst;
        List<Video> videos;
        if (SelectedChannel == null)
            videos = await _db.GetUnwatchedVideosForListAsync(SelectedList.Id);
        else if (SelectedChannel.ShowWatched)
            videos = await _db.GetAllVideosForChannelAsync(SelectedChannel.Id, sortOrder);
        else
            videos = await _db.GetUnwatchedVideosForChannelAsync(SelectedChannel.Id, sortOrder);

        foreach (var v in videos)
            Videos.Add(new VideoViewModel(v, _db, () => RemoveVideoIfFiltered(v.Id)));

        OnPropertyChanged(nameof(HasNoVideos));
        StatusMessage = $"{Videos.Count} video(s) loaded.";
        IsBusy = false;
    }

    public async Task MoveChannelToListAsync(ChannelItem channel, ChannelListItem targetList)
    {
        if (SelectedList == null || targetList.Id == SelectedList.Id) return;
        IsBusy = true;
        StatusMessage = $"Moving \"{channel.Name}\" to \"{targetList.Name}\"...";
        try
        {
            await _db.MoveChannelBetweenListsAsync(channel.Id, SelectedList.Id, targetList.Id);
            StatusMessage = $"Removing channel \"{channel.Name}\"...";
            Channels.Remove(channel);
            StatusMessage = "Refreshing counts...";
            await RefreshChannelCountsAsync();
            StatusMessage = $"Moved \"{channel.Name}\" to \"{targetList.Name}\".";
        }
        finally
        {
            IsBusy = false;
        }
    }

    public void MoveChannel(ChannelItem from, ChannelItem to)
    {
        var fromIdx = Channels.IndexOf(from);
        var toIdx = Channels.IndexOf(to);
        if (fromIdx < 0 || toIdx < 0 || fromIdx == toIdx) return;
        Channels.Move(fromIdx, toIdx);
        _ = SaveChannelOrderAsync();
    }

    private async Task SaveChannelOrderAsync()
    {
        if (SelectedList == null) return;
        var ids = Channels.Select(c => c.Id).ToList();
        await _db.UpdateChannelOrderAsync(SelectedList.Id, ids);
    }

    public void MoveList(ChannelListItem from, ChannelListItem to)
    {
        var fromIdx = Lists.IndexOf(from);
        var toIdx = Lists.IndexOf(to);
        if (fromIdx < 0 || toIdx < 0 || fromIdx == toIdx) return;
        Lists.Move(fromIdx, toIdx);
        _ = SaveListOrderAsync();
    }

    private async Task SaveListOrderAsync()
    {
        var ids = Lists.Select(l => l.Id).ToList();
        await _db.UpdateListOrderAsync(ids);
    }

    private void RemoveVideoIfFiltered(int videoId)
    {
        var vm = Videos.FirstOrDefault(v => v.Id == videoId);
        if (vm == null) return;

        if (vm.Status == VideoStatus.Unwatched)
        {
            // Marked unwatched — count increased, refresh regardless of view mode
            _ = RefreshChannelCountsAsync();
            return;
        }

        if (SelectedChannel?.ShowWatched != true)
        {
            Videos.Remove(vm);
            _ = RefreshChannelCountsAsync();
        }
    }

    private async Task MarkAllWatchedAsync()
    {
        if (SelectedList == null) return;
        var result = MessageBox.Show("Mark all visible videos as watched?", "Confirm", MessageBoxButton.YesNo);
        if (result != MessageBoxResult.Yes) return;

        if (SelectedChannel != null)
            await _db.MarkAllWatchedForChannelAsync(SelectedChannel.Id);
        else
            await _db.MarkAllWatchedForListAsync(SelectedList.Id);

        await RefreshChannelCountsAsync();
        await LoadVideosAsync();
        StatusMessage = "All marked as watched.";
    }

    private async Task AddListAsync()
    {
        var dialog = new Views.InputDialog("Enter list name:", "Add List");
        if (dialog.ShowDialog() != true || string.IsNullOrWhiteSpace(dialog.Result)) return;
        var list = await _db.AddListAsync(dialog.Result.Trim());
        Lists.Add(new ChannelListItem(list));
    }

    private async Task DeleteListAsync()
    {
        if (SelectedList == null) return;
        var result = MessageBox.Show($"Delete list \"{SelectedList.Name}\"?", "Confirm", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (result != MessageBoxResult.Yes) return;
        await _db.DeleteListAsync(SelectedList.Id);
        Lists.Remove(SelectedList);
        SelectedList = null;
    }

    private async Task AddChannelAsync()
    {
        if (SelectedList == null || string.IsNullOrWhiteSpace(AddChannelText)) return;

        var apiKey = _settings.LoadSettings().YouTubeApiKey;
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            MessageBox.Show("Please set your YouTube API key in Settings first.", "No API Key", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        IsBusy = true;
        StatusMessage = "Looking up channel...";
        try
        {
            var info = await _yt.FetchChannelInfoAsync(AddChannelText.Trim(), apiKey);
            var channel = await _db.AddChannelToListAsync(SelectedList.Id, info);
            if (!Channels.Any(c => c.Id == channel.Id))
                Channels.Add(new ChannelItem(channel, _db, OnChannelOptionsChangedAsync));
            AddChannelText = string.Empty;
            StatusMessage = $"Added: {info.Name}";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Error: {GetFullMessage(ex)}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task RemoveChannelAsync()
    {
        if (SelectedList == null || SelectedChannel == null) return;
        var result = MessageBox.Show($"Remove \"{SelectedChannel.Name}\" from this list?", "Confirm", MessageBoxButton.YesNo);
        if (result != MessageBoxResult.Yes) return;

        var channelName = SelectedChannel.Name;
        var listName = SelectedList.Name;
        StatusMessage = $"Removing \"{channelName}\" from \"{listName}\"...";
        await _db.RemoveChannelFromListAsync(SelectedList.Id, SelectedChannel.Id);

        _suppressVideoLoad = true;
        try
        {
            Channels.Remove(SelectedChannel);
            SelectedChannel = null;
        }
        finally
        {
            _suppressVideoLoad = false;
        }

        Videos.Clear();
        OnPropertyChanged(nameof(HasNoVideos));
        await RefreshChannelCountsAsync();
        StatusMessage = $"Removed \"{channelName}\" from \"{listName}\".";
    }

    private async Task RefreshAsync()
    {
        if (SelectedList == null) return;

        var apiKey = _settings.LoadSettings().YouTubeApiKey;
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            MessageBox.Show("Please set your YouTube API key in Settings first.", "No API Key", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        IsBusy = true;
        var maxVideos = _settings.LoadSettings().MaxVideosPerChannel;

        try
        {
            var channels = await _db.GetChannelsForListAsync(SelectedList.Id);
            var memberVideos = await FetchChannelsAsync(channels, apiKey, maxVideos, "Fetching");

            StatusMessage = $"Refresh complete — {channels.Count} channel(s) updated{MemberSuffix(memberVideos)}";
            await RefreshChannelCountsAsync();
            await LoadVideosAsync();
        }
        finally
        {
            IsBusy = false;
        }
    }

    // Shared by Refresh and Refresh All: pulls each channel's uploads via the Data API, then —
    // for channels with Member Content enabled — its members-only videos via InnerTube.
    // Returns the number of members-only videos fetched so the caller can report it.
    private async Task<int> FetchChannelsAsync(List<Channel> channels, string apiKey, int maxVideos, string verb)
    {
        // Reading the session spins up a hidden WebView2 and loads youtube.com, so it happens at
        // most once per refresh, and only once a channel actually asks for member content.
        Dictionary<string, string>? memberCookies = null;
        bool memberSessionUnavailable = false;
        int memberVideos = 0;
        int count = 0;

        foreach (var channel in channels)
        {
            count++;
            StatusMessage = $"{verb} {count}/{channels.Count}: {channel.Name}";

            // Kept in scope: the members-only pass identifies early-access videos by which IDs the
            // public fetch did NOT return, so it needs this exact result set.
            List<VideoInfo> publicVideos = [];
            try
            {
                publicVideos = await _yt.FetchRecentVideosAsync(channel.YouTubeChannelId, apiKey, maxVideos);
                await _db.UpsertVideosAsync(channel.Id, publicVideos);
                await _db.UpdateChannelLastFetchedAsync(channel.Id);
            }
            catch (Exception ex)
            {
                StatusMessage = $"Error on {channel.Name}: {GetFullMessage(ex)}";
                await Task.Delay(1000);
            }

            if (!channel.ShowMembersOnly || memberSessionUnavailable) continue;

            try
            {
                memberCookies ??= await GetYouTubeCookiesAsync();
                if (memberCookies.Count == 0)
                {
                    memberSessionUnavailable = true;
                    StatusMessage = "Not signed in — skipping members-only videos for this refresh.";
                    await Task.Delay(1500);
                    continue;
                }

                try
                {
                    memberVideos += await FetchMemberVideosAsync(channel, memberCookies, apiKey, maxVideos, publicVideos);
                }
                catch (YouTubeSessionExpiredException)
                {
                    StatusMessage = "Your YouTube session has expired — please sign in again...";
                    memberCookies = await ForceReSignInAsync();
                    if (memberCookies.Count == 0)
                    {
                        memberSessionUnavailable = true;
                        StatusMessage = "Sign-in cancelled — skipping members-only videos for this refresh.";
                        await Task.Delay(1500);
                        continue;
                    }
                    memberVideos += await FetchMemberVideosAsync(channel, memberCookies, apiKey, maxVideos, publicVideos);
                }
            }
            catch (Exception ex)
            {
                StatusMessage = $"Members-only fetch failed for {channel.Name}: {GetFullMessage(ex)}";
                await Task.Delay(1000);
            }
        }

        return memberVideos;
    }

    private async Task<int> FetchMemberVideosAsync(
        Channel channel, Dictionary<string, string> cookies, string apiKey, int maxVideos, List<VideoInfo> publicVideos)
    {
        var onBehalfOf = _webView2Cookies.TryGetOnBehalfOfUser();
        var progress = new Progress<string>(msg => StatusMessage = $"{channel.Name}: {msg}");
        var publicIds = publicVideos.Select(v => v.YouTubeVideoId).ToHashSet(StringComparer.Ordinal);
        DateTime? oldestPublic = publicVideos.Count > 0 ? publicVideos.Min(v => v.PublishedAt) : null;

        var videos = await _yt.FetchMembersOnlyVideosAsync(
            channel.YouTubeChannelId, cookies, apiKey, publicIds, oldestPublic, progress, onBehalfOf, maxVideos);
        if (videos.Count > 0)
            await _db.UpsertVideosAsync(channel.Id, videos);
        return videos.Count;
    }

    private static string MemberSuffix(int memberVideos) =>
        memberVideos > 0 ? $", {memberVideos} members-only" : "";

    private async Task ImportTakeoutAsync()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Select watch-history.json from Google Takeout",
            Filter = "JSON files (*.json)|*.json|All files (*.*)|*.*",
            FileName = "watch-history.json"
        };

        if (dialog.ShowDialog() != true) return;

        IsBusy = true;
        StatusMessage = "Parsing watch history file...";
        try
        {
            var watchedIds = await Task.Run(() => _takeout.ParseWatchHistory(dialog.FileName));

            if (watchedIds.Count == 0)
            {
                StatusMessage = "No video IDs found in the file. Make sure you selected watch-history.json.";
                return;
            }

            StatusMessage = $"Found {watchedIds.Count} watched videos, saving to database...";
            await _db.SaveWatchHistoryAsync(watchedIds);
            var marked = await _db.MarkWatchedByYouTubeIdsAsync(watchedIds);
            StatusMessage = $"Import complete — {watchedIds.Count} IDs saved, {marked} video(s) marked as watched.";

            await RefreshChannelCountsAsync();
            await LoadVideosAsync();
        }
        catch (Exception ex)
        {
            StatusMessage = $"Import failed: {GetFullMessage(ex)}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task SyncWatchHistoryAsync()
    {
        IsBusy = true;
        StatusMessage = "Reading YouTube session...";
        try
        {
            var browserCookies = await GetYouTubeCookiesAsync();
            if (browserCookies.Count == 0)
            {
                StatusMessage = "YouTube sign-in cancelled.";
                return;
            }

            var knownIds = await _db.GetAllWatchHistoryIdsAsync();
            var progress = new Progress<string>(msg => StatusMessage = msg);

            List<string> allFetchedIds;
            try
            {
                var onBehalfOf = _webView2Cookies.TryGetOnBehalfOfUser();
                allFetchedIds = await _yt.FetchWatchHistoryViaInnerTubeAsync(browserCookies, progress, onBehalfOf);
            }
            catch (YouTubeSessionExpiredException)
            {
                StatusMessage = "Your YouTube session has expired — please sign in again...";
                browserCookies = await ForceReSignInAsync();
                if (browserCookies.Count == 0)
                {
                    StatusMessage = "YouTube sign-in cancelled.";
                    return;
                }
                var onBehalfOf = _webView2Cookies.TryGetOnBehalfOfUser();
                allFetchedIds = await _yt.FetchWatchHistoryViaInnerTubeAsync(browserCookies, progress, onBehalfOf);
            }

            if (allFetchedIds.Count == 0)
            {
                StatusMessage = "No watch history returned — debug files saved to %TEMP%\\YouTubeToolLogs\\yt_history_p*.json. Check that you are signed in.";
                return;
            }

            StatusMessage = $"Fetched {allFetchedIds.Count} IDs from YouTube history, updating...";

            var newIds = allFetchedIds.Where(id => !knownIds.Contains(id)).ToList();
            if (newIds.Count > 0)
                await _db.SaveWatchHistoryAsync(newIds);

            var marked = await _db.MarkWatchedByYouTubeIdsAsync(allFetchedIds);
            StatusMessage = newIds.Count > 0
                ? $"Sync complete — fetched {allFetchedIds.Count} IDs, {newIds.Count} new, {marked} video(s) marked as watched."
                : $"Sync complete — fetched {allFetchedIds.Count} IDs, {marked} video(s) marked as watched.";

            await RefreshChannelCountsAsync();
            await LoadVideosAsync();
        }
        catch (Exception ex)
        {
            StatusMessage = $"Sync failed: {GetFullMessage(ex)}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task ExportListAsync()
    {
        if (SelectedList == null) return;

        var channels = await _db.GetChannelsForListAsync(SelectedList.Id);

        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = "Export List",
            Filter = "YouTubeTool List (*_YTT.xml)|*_YTT.xml|XML files (*.xml)|*.xml",
            FileName = $"{SelectedList.Name}_YTT.xml"
        };

        if (dialog.ShowDialog() != true) return;

        var doc = new System.Xml.Linq.XDocument(
            new System.Xml.Linq.XDeclaration("1.0", "utf-8", null),
            new System.Xml.Linq.XElement("YouTubeToolList",
                new System.Xml.Linq.XAttribute("name", SelectedList.Name),
                channels.Select(c => new System.Xml.Linq.XElement("Channel",
                    new System.Xml.Linq.XAttribute("name", c.Name),
                    new System.Xml.Linq.XAttribute("youtubeChannelId", c.YouTubeChannelId),
                    c.ThumbnailUrl != null ? new System.Xml.Linq.XAttribute("thumbnailUrl", c.ThumbnailUrl) : null
                ))
            )
        );

        await Task.Run(() => doc.Save(dialog.FileName));
        StatusMessage = $"Exported {channels.Count} channel(s) to {System.IO.Path.GetFileName(dialog.FileName)}.";
    }

    private async Task ImportListAsync()
    {
        var openDialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Import List",
            Filter = "YouTubeTool List (*_YTT.xml)|*_YTT.xml|XML files (*.xml)|*.xml"
        };

        if (openDialog.ShowDialog() != true) return;

        System.Xml.Linq.XDocument doc;
        try
        {
            doc = System.Xml.Linq.XDocument.Load(openDialog.FileName);
        }
        catch (Exception ex)
        {
            StatusMessage = $"Import failed: {GetFullMessage(ex)}";
            return;
        }

        var root = doc.Root;
        if (root?.Name.LocalName != "YouTubeToolList")
        {
            StatusMessage = "Import failed: not a valid YouTubeTool list file.";
            return;
        }

        var xmlListName = root.Attribute("name")?.Value ?? "Imported List";
        var channels = root.Elements("Channel")
            .Select(e => new ChannelInfo(
                e.Attribute("youtubeChannelId")?.Value ?? "",
                e.Attribute("name")?.Value ?? "",
                e.Attribute("thumbnailUrl")?.Value))
            .Where(c => !string.IsNullOrWhiteSpace(c.YouTubeChannelId))
            .ToList();

        if (channels.Count == 0)
        {
            StatusMessage = "Import failed: no valid channels found in the file.";
            return;
        }

        var nameDialog = new Views.InputDialog("Enter name for the new list:", "Import List", xmlListName);
        if (nameDialog.ShowDialog() != true || string.IsNullOrWhiteSpace(nameDialog.Result)) return;

        IsBusy = true;
        StatusMessage = $"Importing {channels.Count} channel(s)...";
        try
        {
            var list = await _db.AddListAsync(nameDialog.Result.Trim());
            var listItem = new ChannelListItem(list);
            Lists.Add(listItem);

            foreach (var info in channels)
                await _db.AddChannelToListAsync(list.Id, info);

            StatusMessage = $"Imported \"{list.Name}\" with {channels.Count} channel(s).";
            SelectedList = listItem;
        }
        catch (Exception ex)
        {
            StatusMessage = $"Import failed: {GetFullMessage(ex)}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task RefreshAllAsync()
    {
        var apiKey = _settings.LoadSettings().YouTubeApiKey;
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            MessageBox.Show("Please set your YouTube API key in Settings first.", "No API Key", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        IsBusy = true;
        var maxVideos = _settings.LoadSettings().MaxVideosPerChannel;

        try
        {
            var targetListIds = GetTargetListIdsForRefreshMode();
            List<Channel> channelsToRefresh;
            string scopeLabel;

            if (targetListIds == null)
            {
                channelsToRefresh = await _db.GetAllChannelsAsync();
                scopeLabel = "all lists";
            }
            else
            {
                if (targetListIds.Count == 0)
                {
                    StatusMessage = "No lists in selected scope.";
                    return;
                }
                channelsToRefresh = await _db.GetChannelsForListsAsync(targetListIds);
                scopeLabel = $"{targetListIds.Count} list(s)";
            }

            if (channelsToRefresh.Count == 0)
            {
                StatusMessage = "No channels found in selected scope.";
                return;
            }

            var memberVideos = await FetchChannelsAsync(channelsToRefresh, apiKey, maxVideos, "Refreshing");

            StatusMessage = $"{RefreshAllButtonText[2..]} complete — {channelsToRefresh.Count} channel(s) updated across {scopeLabel}{MemberSuffix(memberVideos)}";
            await RefreshChannelCountsAsync();
            await LoadVideosAsync();
        }
        finally
        {
            IsBusy = false;
        }
    }

    private List<int>? GetTargetListIdsForRefreshMode()
    {
        if (_refreshMode == RefreshMode.All) return null;

        var listIds = Lists.Select(l => l.Id).ToList();
        if (listIds.Count == 0) return [];

        return _refreshMode switch
        {
            RefreshMode.Top1 => listIds.Take(1).ToList(),
            RefreshMode.Top2 => listIds.Take(2).ToList(),
            RefreshMode.Top3 => listIds.Take(3).ToList(),
            RefreshMode.FirstHalf => listIds.Take((listIds.Count + 1) / 2).ToList(),
            RefreshMode.SecondHalf => listIds.Skip((listIds.Count + 1) / 2).ToList(),
            _ => null,
        };
    }

    private async Task LoadFromSubscriptionsAsync()
    {
        IsBusy = true;
        StatusMessage = "Reading YouTube session...";
        try
        {
            var browserCookies = await GetYouTubeCookiesAsync();
            if (browserCookies.Count == 0)
            {
                StatusMessage = "YouTube sign-in cancelled.";
                return;
            }

            var progress = new Progress<string>(msg => StatusMessage = msg);

            List<ChannelInfo> channels;
            try
            {
                var onBehalfOf = _webView2Cookies.TryGetOnBehalfOfUser();
                channels = await _yt.FetchSubscribedChannelsViaInnerTubeAsync(browserCookies, progress, onBehalfOf);
            }
            catch (YouTubeSessionExpiredException)
            {
                StatusMessage = "Your YouTube session has expired — please sign in again...";
                browserCookies = await ForceReSignInAsync();
                if (browserCookies.Count == 0)
                {
                    StatusMessage = "YouTube sign-in cancelled.";
                    return;
                }
                var onBehalfOf = _webView2Cookies.TryGetOnBehalfOfUser();
                channels = await _yt.FetchSubscribedChannelsViaInnerTubeAsync(browserCookies, progress, onBehalfOf);
            }

            if (channels.Count == 0)
            {
                StatusMessage = "No subscribed channels found. Debug file saved to %TEMP%\\YouTubeToolLogs\\yt_subscriptions_debug.json.";
                return;
            }

            // Filter out channels already in any existing list
            var existingIds = (await _db.GetAllChannelsAsync())
                .Select(c => c.YouTubeChannelId)
                .ToHashSet(StringComparer.Ordinal);

            var newChannels = channels
                .Where(c => !existingIds.Contains(c.YouTubeChannelId))
                .ToList();

            int skipped = channels.Count - newChannels.Count;

            if (newChannels.Count == 0)
            {
                StatusMessage = $"All {channels.Count} subscribed channel(s) are already in your lists.";
                return;
            }

            // Continue numbering from after the highest existing "YouTube Subs N" list
            int nextListNumber = Lists
                .Select(l => l.Name)
                .Where(n => n.StartsWith("YouTube Subs ", StringComparison.OrdinalIgnoreCase))
                .Select(n => int.TryParse(n["YouTube Subs ".Length..], out var num) ? num : 0)
                .DefaultIfEmpty(0)
                .Max() + 1;

            StatusMessage = $"Found {newChannels.Count} new channel(s) ({skipped} already in lists), creating lists...";

            const int batchSize = 30;
            int listsCreated = 0;
            for (int i = 0; i < newChannels.Count; i += batchSize)
            {
                var batch = newChannels.Skip(i).Take(batchSize).ToList();
                var listName = $"YouTube Subs {nextListNumber++}";
                StatusMessage = $"Creating \"{listName}\" ({batch.Count} channels)...";
                var list = await _db.AddListAsync(listName);
                Lists.Add(new ChannelListItem(list));
                foreach (var info in batch)
                    await _db.AddChannelToListAsync(list.Id, info);
                listsCreated++;
            }

            var skippedNote = skipped > 0 ? $", {skipped} already in lists" : "";
            StatusMessage = $"Done — created {listsCreated} list(s) with {newChannels.Count} new channel(s){skippedNote}.";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Load subscriptions failed: {GetFullMessage(ex)}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void OpenSettings()
    {
        var win = new Views.SettingsWindow
        {
            Owner = Application.Current.MainWindow
        };
        win.ShowDialog();
        UiScale = _settings.LoadSettings().UiScale;
    }

    private void ShowMessageHistory()
    {
        var win = new Views.MessageHistoryWindow(Enumerable.Reverse(_messageHistory), UiScale)
        {
            Owner = Application.Current.MainWindow
        };
        win.Show();
    }

    // Use the stored WebView2 session if one exists; otherwise prompt for sign-in.
    // Chrome/Edge/Brave cookies are no longer used as a fallback — they bypass
    // brand account switching and prevent Clear Session from prompting again.
    private async Task<Dictionary<string, string>> GetYouTubeCookiesAsync()
    {
        var owner = Application.Current.MainWindow;
        return await _webView2Cookies.GetYouTubeCookiesAsync(owner);
    }

    // The stored cookies still contain SAPISID after a session expires, so the normal
    // GetYouTubeCookiesAsync path won't re-prompt on its own. Sign out first to clear them,
    // then sign in fresh. Returns the new cookies, or empty if the user cancelled.
    private async Task<Dictionary<string, string>> ForceReSignInAsync()
    {
        await _webView2Cookies.SignOutAsync();
        return await GetYouTubeCookiesAsync();
    }

    private static string GetFullMessage(Exception ex)
    {
        var parts = new List<string>();
        for (var e = (Exception?)ex; e != null; e = e.InnerException)
            if (!string.IsNullOrWhiteSpace(e.Message))
                parts.Add(e.Message);
        return string.Join(" → ", parts);
    }
}
