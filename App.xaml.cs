using System.IO;
using System.Windows;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using YouTubeTool.Data;
using YouTubeTool.Services;
using YouTubeTool.ViewModels;

namespace YouTubeTool;

public partial class App : Application
{
    private ServiceProvider? _serviceProvider;

    public static SettingsService SettingsService { get; private set; } = null!;
    public static YouTubeService YouTubeService { get; private set; } = null!;
    public static GoogleAuthService GoogleAuthService { get; private set; } = null!;
    public static TakeoutImportService TakeoutImportService { get; private set; } = null!;
    public static WebView2CookieService WebView2CookieService { get; private set; } = null!;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Catch unhandled exceptions on the UI thread
        DispatcherUnhandledException += (_, args) =>
        {
            ShowError(args.Exception);
            args.Handled = true;
        };

        // Catch unhandled exceptions on background threads
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            if (args.ExceptionObject is Exception ex)
                ShowError(ex);
        };

        // Catch unhandled exceptions in async Tasks
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            ShowError(args.Exception);
            args.SetObserved();
        };

        var dbPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "YouTubeTool", "YouTubeTool.db");

        Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);

        var services = new ServiceCollection();

        services.AddDbContextFactory<AppDbContext>(options =>
            options.UseSqlite($"Data Source={dbPath}"));

        services.AddSingleton<SettingsService>();
        services.AddSingleton<YouTubeService>();
        services.AddSingleton<GoogleAuthService>();
        services.AddSingleton<TakeoutImportService>();
        services.AddSingleton<ChromeCookieService>();
        services.AddSingleton<WebView2CookieService>();
        services.AddSingleton<DatabaseService>();
        services.AddTransient<MainViewModel>();

        _serviceProvider = services.BuildServiceProvider();

        // Apply migrations
        var factory = _serviceProvider.GetRequiredService<IDbContextFactory<AppDbContext>>();
        await using var db = await factory.CreateDbContextAsync();
        await db.Database.MigrateAsync();

        // Expose singletons for windows that need them
        SettingsService = _serviceProvider.GetRequiredService<SettingsService>();
        YouTubeService = _serviceProvider.GetRequiredService<YouTubeService>();
        GoogleAuthService = _serviceProvider.GetRequiredService<GoogleAuthService>();
        TakeoutImportService = _serviceProvider.GetRequiredService<TakeoutImportService>();
        WebView2CookieService = _serviceProvider.GetRequiredService<WebView2CookieService>();

        // Silently restore saved OAuth session if credentials are configured
        var savedSettings = SettingsService.LoadSettings();
        await GoogleAuthService.TryRestoreSessionAsync(
            savedSettings.OAuthClientId, savedSettings.OAuthClientSecret);

        var mainWindow = new MainWindow(_serviceProvider.GetRequiredService<MainViewModel>());
        mainWindow.Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _serviceProvider?.Dispose();
        base.OnExit(e);
    }

    private static readonly string LogPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "YouTubeTool", "error.log");

    private static int _showingError;

    private static void ShowError(Exception ex)
    {
        // Only one error dialog at a time, thread-safe
        if (System.Threading.Interlocked.CompareExchange(ref _showingError, 1, 0) != 0) return;

        var message = $"{ex.GetType().Name}: {ex.Message}";
        if (ex.InnerException != null)
            message += $"\n\nInner: {ex.InnerException.GetType().Name}: {ex.InnerException.Message}";
        message += $"\n\nStack trace:\n{ex.StackTrace}";

        // Write to log file (safe from any thread)
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);
            File.AppendAllText(LogPath, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}]\n{message}\n\n{new string('-', 80)}\n\n");
        }
        catch { }

        // Dialog must run on the UI thread or it deadlocks WPF
        var display = message + $"\n\n(Also logged to: {LogPath})";
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher != null && !dispatcher.CheckAccess())
            dispatcher.Invoke(() => new Views.ErrorDialog(display).ShowDialog());
        else
            new Views.ErrorDialog(display).ShowDialog();

        System.Threading.Interlocked.Exchange(ref _showingError, 0);
    }
}
