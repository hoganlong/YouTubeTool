# YouTubeTool

A WPF desktop app (.NET 10, Windows) for managing YouTube channel watchlists. Tracks unwatched videos across multiple channels organized into named lists.

---

## What It Does

- Organize YouTube channels into named **Lists**
- **Refresh** any list or channel to pull recent videos from the YouTube API
- Videos display oldest-first so you watch in order
- Mark each video as **Watched**, **Skip** (DontWatch), or **Not Interested** (✕)
- **Show Watched** toggle to reveal/hide watched videos
- **Mark All Watched** button to bulk-clear a list
- Detects **YouTube Shorts** (≤3 min) — shows `(SHORT)` after title, portrait thumbnail
- **Import Takeout** — import your Google Takeout watch-history.json to auto-mark already-watched videos
- Persists all state in SQLite

---

## Setup Requirements

### 1. YouTube Data API Key (required for Refresh)
1. Go to [Google Cloud Console](https://console.cloud.google.com/)
2. Create a project → Enable **YouTube Data API v3**
3. Create an **API Key** credential
4. Paste it into **Settings** (⚙ Settings button, bottom-left)

### 2. OAuth Credentials (required for Sync Watch History button — currently non-functional, see Known Issues)
1. In Google Cloud Console → Credentials → Create **OAuth 2.0 Client ID** (Desktop app)
2. Under OAuth Consent Screen → Add your Google account email as a **Test User** (must be lowercase)
3. Paste Client ID and Client Secret into Settings

### 3. Google Takeout (recommended for marking watched videos)
1. Go to [Google Takeout](https://takeout.google.com/)
2. Select only **YouTube and YouTube Music** → **watch-history.json**
3. Download and extract
4. In the app: click **📂 Import Takeout** and select `watch-history.json`
5. The app saves all imported video IDs to the DB — new channels added later will automatically show already-watched videos as Watched

---

## Running the App

```
cd D:\Projects\YouTubeTool
dotnet run
```

Or open in Visual Studio / Rider and run from there.

---

## Project Structure

```
YouTubeTool/
├── Models/
│   ├── ChannelList.cs          # EF entity — named list of channels
│   ├── Channel.cs              # EF entity — YouTube channel
│   ├── Video.cs                # EF entity — video with Status, IsShort
│   ├── WatchHistoryEntry.cs    # EF entity — imported watch history IDs
│   └── AppSettings.cs          # POCO — API key, OAuth creds, MaxVideos
│
├── Data/
│   ├── AppDbContext.cs         # EF DbContext (Videos, Channels, ChannelLists, WatchHistory)
│   └── AppDbContextFactory.cs  # Design-time factory for EF migrations
│
├── Services/
│   ├── YouTubeService.cs       # YouTube API calls (channels, videos, Shorts detection)
│   ├── DatabaseService.cs      # All DB read/write operations
│   ├── SettingsService.cs      # Loads/saves settings.json
│   ├── GoogleAuthService.cs    # OAuth sign-in via GoogleWebAuthorizationBroker
│   └── TakeoutImportService.cs # Parses watch-history.json from Google Takeout
│
├── ViewModels/
│   ├── MainViewModel.cs        # Main window: lists, channels, videos, all commands
│   ├── VideoViewModel.cs       # Per-video: status commands, DisplayTitle, IsShort
│   ├── SettingsViewModel.cs    # Settings window logic
│   ├── BaseViewModel.cs        # INotifyPropertyChanged base
│   └── RelayCommand.cs         # ICommand implementations (sync + async)
│
├── Views/
│   ├── SettingsWindow.xaml     # API key + OAuth settings dialog
│   ├── InputDialog.xaml        # Generic text-input dialog (used for Add List)
│   └── ErrorDialog.xaml        # Custom error dialog (Copy + Exit buttons)
│
├── Converters/
│   └── TestResultColorConverter.cs  # Also contains BoolToVisibilityConverter,
│                                    # IsShortToViewboxConverter (unused now but present)
│
├── MainWindow.xaml             # 3-panel layout: Lists | Channels | Videos
├── App.xaml.cs                 # DI setup, EF migration on startup, global error handling
│
└── Migrations/
    ├── InitialCreate           # Base schema
    ├── AddWatchHistory         # WatchHistory table
    └── AddIsShort              # IsShort column on Videos
```

---

## Database

- Location: `%APPDATA%\YouTubeTool\YouTubeTool.db`
- Migrations run automatically on startup

**Tables:**
| Table | Purpose |
|---|---|
| ChannelLists | Named lists |
| Channels | YouTube channels |
| ChannelListChannel | Join table (many-to-many) |
| Videos | Video records with status |
| WatchHistory | Imported video IDs (from Takeout) |

**VideoStatus enum:** `Unwatched=0, Watched=1, DontWatch=2, NotInterested=3`

---

## YouTube API Quota Usage

Each Refresh call costs:
- **1 unit** — `channels.list` to get uploads playlist ID
- **1 unit per 50 videos** — `playlistItems.list` to get video list
- **1 unit per 50 videos** — `videos.list` with `contentDetails` for duration (Shorts detection)

Daily quota is 10,000 units. Refreshing a channel with 50 videos costs ~3 units.

---

## YouTube Shorts Detection

Shorts are detected by calling `videos.list` with `contentDetails` to get each video's duration. Any video ≤ 180 seconds (3 minutes) is treated as a Short.

Shorts get:
- `(SHORT)` appended to the title
- Portrait thumbnail URL (`https://i.ytimg.com/vi/{id}/oar2.jpg`) — original aspect ratio, no pillarboxing
- A **32×57 portrait thumbnail box** in the UI (vs 88×50 landscape for regular videos)

---

## Known Issues / Gotchas

### Watch History Sync (Sync Watch History button)
Google restricts the `watchHistory` playlist API endpoint — it returns empty results even with valid OAuth. The button exists but will always report no results. **Use Google Takeout import instead.**

### OAuth "Access blocked" error
If you see "YouTubeTool has not completed the Google verification process":
- Go to Google Cloud Console → OAuth Consent Screen → Test Users
- Add your Google account email (must be **lowercase**)

### Thumbnails
- Regular videos use the `Medium` thumbnail (320×180, 16:9)
- Shorts use `oar2.jpg` (portrait, original aspect ratio) — displayed in a portrait box
- The `Default__` thumbnail from the playlist API always returns 120×90 regardless of video type, so dimension-based Shorts detection doesn't work

### Name collision
`YouTubeTool.Services.YouTubeService` has the same name as `Google.Apis.YouTube.v3.YouTubeService`. Resolved with:
```csharp
using GoogleYT = Google.Apis.YouTube.v3;
```

### EF Core Include after SelectMany
EF Core 10 does not support `.Include()` after `.SelectMany()`. Queries that need related data must be split into two separate queries (get IDs first, then query with Include).

---

## Settings File

Location: `%APPDATA%\YouTubeTool\settings.json`

```json
{
  "YouTubeApiKey": "...",
  "MaxVideosPerChannel": 50,
  "OAuthClientId": "...",
  "OAuthClientSecret": "..."
}
```

OAuth token is cached by Google's library at:
`%APPDATA%\YouTubeTool\oauth_token\Google.Apis.Auth.OAuth2.Responses.TokenResponse-user`
