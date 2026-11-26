using System.Net.Http;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using LyricsIsland.Core;

namespace LyricsIsland.Windows;

/// <summary>
/// "Search Lyrics Manually…" (port of <c>manualSearch()</c> in AppDelegate.swift): a title and artist go to
/// LRCLIB's free-text search; the top synced result becomes the lyrics for the song that was playing.
/// </summary>
public sealed class ManualSearchWindow : Window
{
    private readonly IslandModel _model;
    private readonly LyricsService _lyrics;
    private readonly Track _track;
    private readonly TextBox _query = new() { Margin = new Thickness(0, 10, 0, 0), Padding = new Thickness(4, 3, 4, 3) };
    private readonly TextBlock _status = new() { Margin = new Thickness(0, 8, 0, 0), TextWrapping = TextWrapping.Wrap, Foreground = System.Windows.Media.Brushes.DimGray };
    private readonly Button _search = new() { Content = "Search", IsDefault = true, MinWidth = 80, Margin = new Thickness(0, 0, 8, 0) };

    private static ManualSearchWindow? _open;

    private ManualSearchWindow(IslandModel model, LyricsService lyrics, Track track)
    {
        _model = model;
        _lyrics = lyrics;
        _track = track;
        Title = "Search Lyrics";
        Width = 420;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Topmost = true;
        ShowInTaskbar = true;

        _query.Text = $"{track.Title} {LyricsService.PrimaryArtist(track.Artist)}";
        var cancel = new Button { Content = "Cancel", IsCancel = true, MinWidth = 80 };
        _search.Click += async (_, _) => await SearchAsync();
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 14, 0, 0), Children = { _search, cancel } };
        Content = new StackPanel
        {
            Margin = new Thickness(16),
            Children =
            {
                new TextBlock { Text = "Type a title and artist. The top LRCLIB result with synced lyrics will be used for this song.", TextWrapping = TextWrapping.Wrap },
                _query,
                _status,
                buttons,
            },
        };
        Loaded += (_, _) =>
        {
            Activate();
            _query.Focus();
            _query.SelectAll();
        };
        Closed += (_, _) => _open = null;
    }

    /// <summary>Opens the search for the current song (or brings an open one to the front).</summary>
    public static void Show(IslandModel model, LyricsService lyrics)
    {
        if (_open is { } open)
        {
            open.Activate();
            return;
        }
        if (model.Track is not { } track || track.Id.StartsWith("demo:", StringComparison.Ordinal)) return;
        _open = new ManualSearchWindow(model, lyrics, track);
        _open.Show();
    }

    private async Task SearchAsync()
    {
        var query = _query.Text.Trim();
        if (query.Length == 0) return;
        _search.IsEnabled = false;
        _status.Text = "Searching…";
        Mouse.OverrideCursor = Cursors.AppStarting;
        try
        {
            var raw = await _lyrics.SearchAsync(query);
            if (raw is null)
            {
                _status.Text = $"LRCLIB has no synced lyrics for “{query}”.";
                return;
            }
            var lyrics = _lyrics.StoreManual(raw, _track);
            _model.SetManualLyrics(lyrics, _track.Id);
            Close();
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
        {
            _status.Text = $"Lyrics search failed: {e.Message}";
        }
        finally
        {
            Mouse.OverrideCursor = null;
            _search.IsEnabled = true;
        }
    }
}
