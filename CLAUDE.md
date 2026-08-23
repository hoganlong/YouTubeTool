# CLAUDE.md

Guidance for working in this repo. Keep it current when conventions or architecture change.

## What this is

WPF desktop app (.NET 10, Windows-only) for managing YouTube channel watchlists. Channels are organized into named lists; the app tracks unwatched videos and lets you mark them Watched / Skip / Not Interested. State lives in SQLite via EF Core. See `README.md` for the user-facing feature list and setup.

## Build & run

```
dotnet build              # debug build
dotnet run                # launch
dotnet publish -c Release # self-contained single-file win-x64
```

**Always close the running app before building.** The build copies `YouTubeTool.exe`; if the app is open it fails with a file-lock error (`MSB3027`), not a compile error. A build that reports only `MSB3026/3027/3021` copy failures actually compiled fine — the `.dll` built; only the `.exe` copy was blocked.

EF migrations run automatically on startup (`App.OnStartup`). To add one: `dotnet ef migrations add <Name>` (design-time uses `AppDbContextFactory`).

## Architecture

- **MVVM, hand-rolled** — no MVVM framework. `BaseViewModel` (INotifyPropertyChanged) + `RelayCommand` (sync & async). DI via `Microsoft.Extensions.DependencyInjection`, wired in `App.xaml.cs`.
- `Models/` — EF entities (`ChannelList`, `Channel`, `Video`, `WatchHistoryEntry`) + `AppSettings` POCO.
- `Data/` — `AppDbContext`, `AppDbContextFactory` (design-time).
- `Services/` — `YouTubeService` (API + InnerTube), `DatabaseService` (all DB I/O), `SettingsService`, `GoogleAuthService`, `TakeoutImportService`, `ChromeCookieService`, `WebView2CookieService`.
- `ViewModels/` — `MainViewModel` is the hub (lists, channels, videos, all commands). `RefreshMode` enum lives here.
- `Views/` — `MainWindow` (3-pane: Lists | Channels | Videos), `SettingsWindow`, dialogs, `YouTubeLoginWindow`, `MessageHistoryWindow`.

**Data locations:** DB at `%APPDATA%\YouTubeTool\YouTubeTool.db`, settings at `%APPDATA%\YouTubeTool\settings.json`.

## Conventions

- **Debug/log files go in `%TEMP%\YouTubeToolLogs\`** — never the `%TEMP%` root. Create the dir with `Directory.CreateDirectory` (wrapped in try/catch) before writing.
- **Commit messages** are version-prefixed: `vX.Y.Z - short summary`, followed by a body of bullet points. Bump `<Version>`, `<AssemblyVersion>`, `<FileVersion>` in `YouTubeTool.csproj` together for a release commit (the titlebar reads the assembly version). **No `Co-Authored-By` lines** — use the author's name only.
- Match the surrounding code's comment density: non-obvious workarounds get a short "why" comment.

## Gotchas (the non-obvious stuff)

- **InnerTube expired-session trap** — Sync Watch History and Load Subscriptions use YouTube's InnerTube API with the WebView2 browser session. When that session expires, YouTube returns **HTTP 200 with a signed-out feed** (`responseContext.mainAppWebResponseContext.loggedOut == true`), even though all cookies are still present on disk. So "SAPISID cookie exists" does **not** mean "signed in." `YouTubeService.IsLoggedOut()` detects this and throws `YouTubeSessionExpiredException`; `MainViewModel` catches it and auto-prompts re-sign-in (`ForceReSignInAsync`). Raw responses dump to `%TEMP%\YouTubeToolLogs\yt_history_p*.json`.
- **Rotating-token session decay** — Google's `__Secure-1PSIDTS`/`__Secure-3PSIDTS` cookies rotate every few hours; a client that only *reads* the stored cookies (never loads a page) lets them go stale and hits the logged-out trap above within hours — even on a healthy, freshly-signed-in session. This bit us after moving to a new machine where no other browser kept the shared account warm. Fix: `WebView2CookieService.ReadCookiesAsync(refreshSession: true)` navigates the hidden WebView2 to `youtube.com` and waits for load *before* reading, so Google re-issues fresh tokens (persisted by WebView2). **Don't "optimize away" that page-load** — it's what keeps the session alive between syncs. The passive Settings status check passes `refreshSession: false` on purpose (no network side effects just to show status). Note: `GetCookiesAsync` can return the same name under multiple domain/path scopes (e.g. `__Secure-YNID`), so cookies are collected last-wins, not via `ToDictionary` (which throws on dupes).
- **Name collision** — `YouTubeTool.Services.YouTubeService` clashes with `Google.Apis.YouTube.v3.YouTubeService`. Resolved with `using GoogleYT = Google.Apis.YouTube.v3;`.
- **EF Core: no `.Include()` after `.SelectMany()`** — split into two queries (get IDs, then query with Include).
- **WPF tooltip UIA-bridge crash** — on some hosts WPF's tooltip path throws `FileNotFoundException` from `PopupSecurityHelper.ForceMsaaToUiaBridge`, sometimes while idle. `App.DispatcherUnhandledException` filters this specific exception and marks it handled (`IsKnownWpfTooltipUiaBridgeException`). Don't remove it.
- **Taskbar icon** — needs a multi-size `.ico` (16–256). If it regresses to a single entry, re-run `rebuild_icon.ps1`. Belt-and-suspenders code: `SetCurrentProcessExplicitAppUserModelID` in `App.OnStartup` + `WM_SETICON` push in `MainWindow.SourceInitialized`.
- **Shorts detection** — `videos.list` with `contentDetails` for duration; ≤180s = Short. Thumbnail dimensions can't be used (the playlist API always returns 120×90).
- **Members-only videos: two detection signals, and both are needed.** They are *not* in a channel's uploads playlist (`UU…`) and an API key authenticates as nobody, so the Data API can never surface them. `FetchMembersOnlyVideosAsync` reads the channel's **Videos, Shorts and Live tabs** over InnerTube with the WebView2 session (inheriting both session traps above, including the re-sign-in retry), and flags an entry when *either*:
  1. it carries a members-only badge (`BADGE_MEMBERS_ONLY` / "Members only") — only ever seen on channels the viewer is **not** a member of, since it's the join prompt; or
  2. it is **absent from the public uploads playlist** — which is what catches members-only *early access* on channels you **are** a member of, where no badge is emitted at all.

  Signal 2 needs its guards or it mislabels the back catalogue: skip anything older than the oldest video the public fetch returned (`MaxVideosPerChannel` caps that fetch while the tab walk goes deeper), and skip premieres/live via `snippet.liveBroadcastContent`. Both were measured, not guessed — without the date floor, 23 and 79 five-year-old public videos got flagged on two test channels. Scan all three tabs: one channel had 3 markers in Videos and 15 in Shorts. InnerTube returns only a *relative* date ("3 weeks ago"), hence the `videos.list` enrichment for real `publishedAt` + duration, with `ParseRelativeDate` as fallback. Diagnostics: `%TEMP%\YouTubeToolLogs\yt_members_summary.txt` plus per-tab page-0 dumps.
- **Dead ends already ruled out for members-only** — don't retry these. There is **no `UUMO`/`UUMF` members-only system playlist**; browsing `VLUUMO…` returns HTTP **200** with `alerts[].alertRenderer` = "The playlist does not exist." (note: 200, not 404, so a status check sails past it and the fetch fails silently). The authenticated `VLUU…` uploads playlist returns exactly what the API key already sees, so it adds nothing. And because a members-only preview may last only hours before the creator makes it public, "channel X has no members-only videos" is a statement about *this moment*, not the channel — don't conclude a channel lacks them from one measurement.
- **Per-channel view options are all phrased as "show"** — `ShowShorts` / `ShowWatched` / `ShowMembersOnly` on `Channel`, surfaced as the three checkboxes in the channel right-click menu. Keep that polarity if you add a fourth: an earlier `HideShorts` inverted against the others and made every filter expression read backwards. Each option filters the videos pane **and** the unwatched counts, so a new one means touching every query in `DatabaseService` that already carries the `(v.Channel.ShowX || !v.IsX)` pattern — miss one and the counts silently disagree with the list. `ShowShorts` defaults to **true**; the other two default to false.
- **`x:Shared="False"` on the channel `ContextMenu`** — a `ContextMenu` handed out from a `ResourceDictionary` is a *single shared instance* by default. Attached to every `ListBoxItem` via the `ItemContainerStyle`, one instance means the per-channel check marks bleed between channels. `x:Shared="False"` gives each row its own menu; the `DataContext` then flows from the owning `ListBoxItem` (the `ChannelItem`), so each menu edits its own channel. Don't inline the menu back into the style setter — that reintroduces the sharing.
