using System.Diagnostics;
using System.Windows.Input;
using YouTubeTool.Models;
using YouTubeTool.Services;

namespace YouTubeTool.ViewModels;

public class VideoViewModel : BaseViewModel
{
    private readonly DatabaseService _db;
    private readonly Action _onStatusChanged;
    private VideoStatus _status;
    private bool _isStarred;

    public int Id { get; }
    public string YouTubeVideoId { get; }
    public string Title { get; }
    public string? ThumbnailUrl { get; }
    public DateTime PublishedAt { get; }
    public string ChannelName { get; }
    public bool IsShort { get; }
    public string DisplayTitle => IsShort ? $"{Title} (SHORT)" : Title;
    public string Url => $"https://www.youtube.com/watch?v={YouTubeVideoId}";

    public VideoStatus Status
    {
        get => _status;
        set => SetProperty(ref _status, value);
    }

    public bool IsStarred
    {
        get => _isStarred;
        set => SetProperty(ref _isStarred, value);
    }

    public ICommand MarkWatchedCommand { get; }
    public ICommand MarkUnwatchedCommand { get; }
    public ICommand MarkDontWatchCommand { get; }
    public ICommand MarkNotInterestedCommand { get; }
    public ICommand OpenInBrowserCommand { get; }
    public ICommand ToggleStarCommand { get; }

    public VideoViewModel(Video video, DatabaseService db, Action onStatusChanged)
    {
        Id = video.Id;
        YouTubeVideoId = video.YouTubeVideoId;
        Title = video.Title;
        ThumbnailUrl = video.ThumbnailUrl;
        PublishedAt = video.PublishedAt;
        ChannelName = video.Channel?.Name ?? string.Empty;
        IsShort = video.IsShort;
        _status = video.Status;
        _isStarred = video.IsStarred;
        _db = db;
        _onStatusChanged = onStatusChanged;

        MarkWatchedCommand = new AsyncRelayCommand(() => SetStatusAsync(VideoStatus.Watched));
        MarkUnwatchedCommand = new AsyncRelayCommand(() => SetStatusAsync(VideoStatus.Unwatched));
        MarkDontWatchCommand = new AsyncRelayCommand(() => SetStatusAsync(VideoStatus.DontWatch));
        MarkNotInterestedCommand = new AsyncRelayCommand(() => SetStatusAsync(VideoStatus.NotInterested));
        OpenInBrowserCommand = new RelayCommand(OpenInBrowser);
        ToggleStarCommand = new AsyncRelayCommand(ToggleStarAsync);
    }

    private async Task SetStatusAsync(VideoStatus status)
    {
        await _db.UpdateVideoStatusAsync(Id, status);
        Status = status;
        _onStatusChanged();
    }

    private async Task ToggleStarAsync()
    {
        var newVal = !IsStarred;
        await _db.UpdateVideoStarredAsync(Id, newVal);
        IsStarred = newVal;
    }

    private void OpenInBrowser()
    {
        Process.Start(new ProcessStartInfo { FileName = Url, UseShellExecute = true });
    }
}
