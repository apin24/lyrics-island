namespace LyricsIsland.Core;

public static class AppInfo
{
    public const string Name = "Lyrics Island";

    /// <summary>"0.1.0", from <c>&lt;Version&gt;</c> in Directory.Build.props.</summary>
    public static string Version { get; } = typeof(AppInfo).Assembly.GetName().Version is { } v
        ? $"{v.Major}.{v.Minor}.{v.Build}"
        : "0.0.0";
}

public static class AppPaths
{
    /// <summary><c>%APPDATA%\LyricsIsland\settings.json</c></summary>
    public static string SettingsFile { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "LyricsIsland", "settings.json");

    /// <summary><c>%LOCALAPPDATA%\LyricsIsland\Cache</c></summary>
    public static string CacheDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LyricsIsland", "Cache");
}
