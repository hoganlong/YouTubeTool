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
