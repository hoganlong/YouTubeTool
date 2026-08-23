# YouTubeTool

A WPF desktop app (.NET 10, Windows) for managing YouTube channel watchlists. Tracks unwatched videos across multiple channels organized into named lists.

---

## What It Does

- Organize YouTube channels into named **Lists**
- Lists display **(x/y)** — x channels with unwatched videos, y total unwatched — so you can see at a glance which lists need attention
- **Refresh** any list to pull recent videos, or use the **split Refresh button** to refresh a chosen subset:
  - Click the main button to refresh using the currently selected scope
  - Click the **▼** arrow on the left to choose **Refresh All**, **Refresh Top 1/2/3**, **Refresh First Half**, or **Refresh Second Half** — the selected option becomes the button's label
- **Load Subscriptions** — import all your YouTube subscriptions via browser sign-in, auto-split into lists of 30 (skips channels already in any list)
- Videos display oldest-first so you watch in order
- Mark each video as **Watched**, **Skip** (DontWatch), or **Not Interested** (✕)
- **Right-click any channel** for its per-channel view options (all saved in the DB). Each one
  controls both the video list and the unwatched counts:
  - **Show Shorts** — on by default; uncheck to hide Shorts for that channel
  - **Show Watched** — off by default; check to reveal watched videos
  - **Show Member** — off by default; check to pull in members-only videos (see below)
- **Mark All Watched** button to bulk-clear a list
- Channel names are **bold** when they have unwatched videos, and wrap to multiple lines if long
- Drag channels between lists, or drag lists themselves to reorder — status bar shows live progress during the move
- Detects **YouTube Shorts** (≤3 min) — shows `(SHORT)` after title, portrait thumbnail
- Members-only videos show `(MEMBERS)` after the title
- **Import Takeout** — import your Google Takeout watch-history.json to auto-mark already-watched videos
- **Sync Watch History** — sign in to YouTube via browser to sync watched videos
- **Message History** — `?` button on the status bar opens a scrollable log of all status messages with copy support
- Persists all state in SQLite

---

## Setup Requirements

### 1. YouTube Data API Key (required for Refresh)

The Refresh button fetches new videos from YouTube. To use it, you need a free YouTube Data API key from Google. You won't be charged — the free quota is more than enough for personal use.

**Step 1 — Create a Google Cloud project**
1. Go to [console.cloud.google.com](https://console.cloud.google.com/)
2. Sign in with any Google account
3. Click the project dropdown at the top of the page (it may say "Select a project")
4. Click **New Project**
5. Give it any name (e.g. `YouTubeTool`) and click **Create**
6. Make sure your new project is selected in the dropdown before continuing

**Step 2 — Enable the YouTube Data API**
1. In the left sidebar, click **APIs & Services** → **Library**
2. Search for `YouTube Data API v3`
3. Click the result, then click **Enable**

**Step 3 — Create an API Key**
1. In the left sidebar, click **APIs & Services** → **Credentials**
2. Click **+ Create Credentials** at the top
3. Choose **API key**
4. Google will show you your new API key — copy it

**Step 4 — Enter the key in YouTubeTool**
1. Open YouTubeTool
2. Click the **⚙ Settings** button in the bottom-left
3. Paste your API key into the **YouTube API Key** field
4. Click **Save**

You're done. Click **↻ Refresh** on any list to start fetching videos.

---

### 2. Google Takeout (recommended for marking watched videos)

If you've been watching YouTube for a while, importing your watch history lets the app automatically mark videos you've already seen.

1. Go to [takeout.google.com](https://takeout.google.com/)
2. Click **Deselect all**, then scroll down and check only **YouTube and YouTube Music**
3. Click the **All YouTube data included** button and uncheck everything except **history**
4. Click **Next step** → **Create export**
5. Download the zip when it's ready and extract it
6. Find the file `watch-history.json` (usually inside `Takeout/YouTube and YouTube Music/history/`)
7. In YouTubeTool, click **📂 Import Takeout** and select that file

The app will mark any videos it already knows about as Watched, and remember all imported IDs so future channel refreshes also auto-mark them.

---

### 3. OAuth Credentials (optional — for Sync Watch History)
1. In Google Cloud Console → Credentials → Create **OAuth 2.0 Client ID** (Desktop app)
2. Under OAuth Consent Screen → Add your Google account email as a **Test User** (must be lowercase)
3. Paste Client ID and Client Secret into Settings

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
    ├── InitialCreate              # Base schema
    ├── AddWatchHistory            # WatchHistory table
    ├── AddIsShort                 # IsShort column on Videos
    ├── AddHideShortsToChannel     # HideShorts column on Channels
    ├── AddChannelViewOptionsAndMembersOnly
    │                              # ShowWatched + IncludeMembersOnly on Channels,
    │                              # IsMembersOnly on Videos
    └── ShowOptionsPerChannel      # HideShorts -> ShowShorts (inverted, preserving behavior),
                                   # IncludeMembersOnly -> ShowMembersOnly
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

## Members-Only Videos

Members-only videos never appear in a channel's public uploads playlist, so a plain Refresh cannot see
them no matter what — an API key isn't authenticated as anyone, let alone as a member of that channel.
Reaching them means using your YouTube browser sign-in.

To pull them in:

1. Right-click the channel → tick **Show Member**
2. Click **↻ Refresh**

The refresh will use your YouTube browser sign-in (the same WebView2 session as Sync Watch History),
prompting you to sign in if there's no valid session. Only channels with the option ticked trigger
this, and the session is read once per refresh no matter how many channels use it.

Once fetched, members-only videos behave like any other video — sorted by publish date, markable as
Watched/Skip/Not Interested — and are labelled `(MEMBERS)`. Unticking the option hides them again
without deleting them.

### The early-access case (the common one)

Many creators post a video to members first and make it public a day or so later. During that window
the video is missing from the app entirely, which is the main reason to turn this on.

Two things follow from that:

- **A refresh has to run while the video is still members-only.** If the creator releases it publicly
  before you refresh, it simply arrives as a normal video.
- **Nothing is duplicated when it goes public.** The ordinary refresh re-imports it with the
  members-only flag cleared, so it moves into your normal list on its own.

### How it decides what's members-only

The app reads the channel's **Videos, Shorts and Live** tabs and flags a video when either:

- it carries a **members-only badge** — YouTube shows this to people who *aren't* members, as a prompt
  to join; or
- it's **missing from the public uploads playlist** — which is how members-only early access looks on a
  channel you *are* a member of, where no badge is shown at all.

The second test ignores anything older than the oldest video the normal Refresh fetched (beyond that
depth the two lists just cover different ranges), and ignores premieres and scheduled live streams.

**Costs:** 1 extra quota unit per 50 members-only videos (a `videos.list` call for real publish dates
and durations).

**If nothing comes back:** most likely the channel has no members-only videos right now — a preview may
already have gone public, or the membership's perks may not include videos at all. Check
`%TEMP%\YouTubeToolLogs\yt_members_summary.txt`, which records each tab scanned, how many videos were
checked, and how many candidates were dropped and why.

---

## YouTube Shorts Detection

Shorts are detected by calling `videos.list` with `contentDetails` to get each video's duration. Any video ≤ 180 seconds (3 minutes) is treated as a Short.

Shorts get:
- `(SHORT)` appended to the title
- Portrait thumbnail URL (`https://i.ytimg.com/vi/{id}/oar2.jpg`) — original aspect ratio, no pillarboxing
- A **32×57 portrait thumbnail box** in the UI (vs 88×50 landscape for regular videos)

---

## Known Issues / Gotchas

### Suspended or deleted channels
If a subscribed channel has been suspended or deleted by YouTube, Refresh and Refresh All will silently skip it (no error shown). The channel remains in your list but will never have new videos.

### Members-only videos and the 400-video limit
`MaxVideosPerChannel` caps how far back each Refresh reads. On a channel with more videos than that,
the older ones are never fetched — and the members-only check deliberately ignores anything older than
that cutoff, because past it the app can't tell "members-only" from "not fetched yet."

### Watch History Sync (Sync Watch History button)
Sync Watch History pulls your history via YouTube's InnerTube API using your **browser sign-in session** (not the OAuth `watchHistory` playlist endpoint, which Google restricts and which always returns empty). Sign in once via the WebView2 login window and it works.

**If your session expires:** YouTube returns a logged-out feed (HTTP 200 but `responseContext.loggedOut = true`, with the "Keep track of what you watch" empty state). The cookies are still present on disk, so the app can't tell from their presence alone. The app now detects the logged-out response, automatically prompts you to sign in again, and retries — so a stale session no longer silently reports "no history." You can also re-auth manually any time via **Settings → Switch Account**.

Raw InnerTube responses are saved to `%TEMP%\YouTubeToolLogs\yt_history_p*.json` (and cookie names to `yt_history_cookies.txt`) for debugging.

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

### WPF tooltip UIA-bridge crash
On some Windows hosts, WPF's tooltip code path (`Popup.PopupSecurityHelper.ForceMsaaToUiaBridge`) P/Invokes into a UI Automation bridge native DLL that isn't always present. When missing, `ToolTipService`'s `DispatcherTimer` throws `FileNotFoundException` — sometimes while the app is idle in the background with no user input. The crash is harmless (the tooltip just doesn't show), so `App.OnStartup` filters this specific exception in `DispatcherUnhandledException` and marks it handled instead of showing the error dialog.

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
