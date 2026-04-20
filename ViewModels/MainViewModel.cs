using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Input;
using YouTubeTool.Models;
using YouTubeTool.Services;

namespace YouTubeTool.ViewModels;

public class ChannelListItem : BaseViewModel
{
    private string _name;
    public int Id { get; }
    public string Name { get => _name; set => SetProperty(ref _name, value); }
    public ChannelListItem(ChannelList list) { Id = list.Id; _name = list.Name; }
}

public class ChannelItem : BaseViewModel
{
    private int _unwatchedCount;
    public int Id { get; }
    public string Name { get; }
    public string YouTubeChannelId { get; }
    public VideoSortOrder SortOrder { get; set; }
    public int UnwatchedCount { get => _unwatchedCount; set => SetProperty(ref _unwatchedCount, value); }
    public string DisplayName => UnwatchedCount > 0 ? $"{Name} ({UnwatchedCount})" : Name;

    public ChannelItem(Channel channel) { Id = channel.Id; Name = channel.Name; YouTubeChannelId = channel.YouTubeChannelId; SortOrder = channel.VideoSortOrder; }

    public void RefreshDisplayName() => OnPropertyChanged(nameof(DisplayName));
}

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
    private bool _showWatched;
    private string _statusMessage = "Ready";
    private string _addChannelText = string.Empty;
    private double _uiScale = 1.0;
    private readonly List<string> _messageHistory = [];

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
    public bool ShowWatched
    {
        get => _showWatched;
        set { if (SetProperty(ref _showWatched, value)) _ = LoadVideosAsync(); }
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

        _uiScale = _settings.LoadSettings().UiScale;

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
            Channels.Add(new ChannelItem(c));

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

        var sortOrder = SelectedChannel?.SortOrder ?? VideoSortOrder.OldestFirst;
        List<Video> videos;
        if (ShowWatched)
        {
            videos = SelectedChannel != null
                ? await _db.GetAllVideosForChannelAsync(SelectedChannel.Id, sortOrder)
                : await _db.GetAllVideosForListAsync(SelectedList.Id);
        }
        else
        {
            videos = SelectedChannel != null
                ? await _db.GetUnwatchedVideosForChannelAsync(SelectedChannel.Id, sortOrder)
                : await _db.GetUnwatchedVideosForListAsync(SelectedList.Id);
        }

        foreach (var v in videos)
            Videos.Add(new VideoViewModel(v, _db, () => RemoveVideoIfFiltered(v.Id)));

        OnPropertyChanged(nameof(HasNoVideos));
        StatusMessage = $"{Videos.Count} video(s) loaded.";
        IsBusy = false;
    }

    public async Task MoveChannelToListAsync(ChannelItem channel, ChannelListItem targetList)
    {
        if (SelectedList == null || targetList.Id == SelectedList.Id) return;
        await _db.MoveChannelBetweenListsAsync(channel.Id, SelectedList.Id, targetList.Id);
        Channels.Remove(channel);
        await RefreshChannelCountsAsync();
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

        if (!ShowWatched)
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
                Channels.Add(new ChannelItem(channel));
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
        await _db.RemoveChannelFromListAsync(SelectedList.Id, SelectedChannel.Id);
        Channels.Remove(SelectedChannel);
        SelectedChannel = null;
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
        int count = 0;

        try
        {
            var channels = await _db.GetChannelsForListAsync(SelectedList.Id);
            foreach (var channel in channels)
            {
                count++;
                StatusMessage = $"Fetching {count}/{channels.Count}: {channel.Name}";
                try
                {
                    var videos = await _yt.FetchRecentVideosAsync(channel.YouTubeChannelId, apiKey, maxVideos);
                    await _db.UpsertVideosAsync(channel.Id, videos);
                    await _db.UpdateChannelLastFetchedAsync(channel.Id);
                }
                catch (Exception ex)
                {
                    StatusMessage = $"Error on {channel.Name}: {GetFullMessage(ex)}";
                    await Task.Delay(1000);
                }
            }

            StatusMessage = $"Refresh complete — {channels.Count} channel(s) updated";
            await RefreshChannelCountsAsync();
            await LoadVideosAsync();
        }
        finally
        {
            IsBusy = false;
        }
    }

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
            var owner = Application.Current.MainWindow;
            var browserCookies = await _webView2Cookies.GetYouTubeCookiesAsync(owner);
            if (browserCookies.Count == 0)
            {
                StatusMessage = "YouTube sign-in cancelled.";
                return;
            }

            var knownIds = await _db.GetAllWatchHistoryIdsAsync();
            var progress = new Progress<string>(msg => StatusMessage = msg);

            var allFetchedIds = await _yt.FetchWatchHistoryViaInnerTubeAsync(browserCookies, progress);

            if (allFetchedIds.Count == 0)
            {
                StatusMessage = "No watch history returned — debug file saved to %TEMP%\\yt_history_debug.json. Check that you are signed in.";
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
            var allChannels = await _db.GetAllChannelsAsync();
            if (allChannels.Count == 0)
            {
                StatusMessage = "No channels found across any list.";
                return;
            }

            int count = 0;
            foreach (var channel in allChannels)
            {
                count++;
                StatusMessage = $"Refreshing {count}/{allChannels.Count}: {channel.Name}";
                try
                {
                    var videos = await _yt.FetchRecentVideosAsync(channel.YouTubeChannelId, apiKey, maxVideos);
                    await _db.UpsertVideosAsync(channel.Id, videos);
                    await _db.UpdateChannelLastFetchedAsync(channel.Id);
                }
                catch (Exception ex)
                {
                    StatusMessage = $"Error on {channel.Name}: {GetFullMessage(ex)}";
                    await Task.Delay(1000);
                }
            }

            StatusMessage = $"Refresh all complete — {allChannels.Count} channel(s) updated";
            await RefreshChannelCountsAsync();
            await LoadVideosAsync();
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task LoadFromSubscriptionsAsync()
    {
        IsBusy = true;
        StatusMessage = "Reading YouTube session...";
        try
        {
            var owner = Application.Current.MainWindow;
            var browserCookies = await _webView2Cookies.GetYouTubeCookiesAsync(owner);
            if (browserCookies.Count == 0)
            {
                StatusMessage = "YouTube sign-in cancelled.";
                return;
            }

            var progress = new Progress<string>(msg => StatusMessage = msg);
            var channels = await _yt.FetchSubscribedChannelsViaInnerTubeAsync(browserCookies, progress);

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

    private static string GetFullMessage(Exception ex)
    {
        var parts = new List<string>();
        for (var e = (Exception?)ex; e != null; e = e.InnerException)
            if (!string.IsNullOrWhiteSpace(e.Message))
                parts.Add(e.Message);
        return string.Join(" → ", parts);
    }
}
