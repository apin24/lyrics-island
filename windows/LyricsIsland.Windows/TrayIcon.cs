using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using LyricsIsland.Core;
using Forms = System.Windows.Forms;

namespace LyricsIsland.Windows;

/// <summary>What the tray menu needs from the app.</summary>
public interface ITrayHost
{
    IslandModel Model { get; }
    IslandSettings Settings { get; }

    /// <summary>The demo player is running (by setting or by <c>--demo</c>).</summary>
    bool UsingDemo { get; }

    /// <summary><c>--demo</c> forces demo mode; the menu item is then disabled.</summary>
    bool DemoForced { get; }

    /// <summary>Whether Ctrl+Alt+L could be registered.</summary>
    bool HotkeyRegistered { get; }

    void SearchLyricsManually();
    void Quit();
}

/// <summary>
/// Notification-area icon and menu, port of the status item in AppDelegate.swift. The menu is rebuilt each
/// time it opens, so labels (now playing, timing offset) and check marks are always current; every change
/// goes to <see cref="IslandSettings"/>, which saves itself and takes effect at once.
/// </summary>
internal sealed partial class TrayIcon : IDisposable
{
    private readonly ITrayHost _host;
    private readonly Forms.NotifyIcon _icon;
    private readonly Forms.ContextMenuStrip _menu = new() { ShowCheckMargin = true, ShowImageMargin = false };
    private readonly Icon _image;

    public TrayIcon(ITrayHost host)
    {
        _host = host;
        _image = CreateBubbleIcon();
        _menu.Opening += (_, _) => Build();
        Build(); // so the menu has items before it first opens (keyboard access, tests)
        _icon = new Forms.NotifyIcon { Icon = _image, Text = "Lyrics Island", ContextMenuStrip = _menu, Visible = true };
    }

    /// <summary>Shows the menu at a screen point (screenshots, <c>--show-menu</c>).</summary>
    public void ShowMenuAt(int x, int y)
    {
        Build();
        _menu.Show(new Point(x, y));
    }

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
        _menu.Dispose();
        _image.Dispose();
    }

    private void Build()
    {
        var model = _host.Model;
        var settings = _host.Settings;
        var items = _menu.Items;
        items.Clear();

        var header = new Forms.ToolStripMenuItem("Lyrics Island") { Enabled = false };
        header.Font = new Font(header.Font, FontStyle.Bold);
        items.Add(header);

        var nowPlaying = model.Track is { } t && model.Playback != PlaybackState.Stopped
            ? $"Now playing: {t.Title} — {t.Artist}"
            : _host.UsingDemo ? "Demo mode" : "Spotify isn't playing";
        items.Add(new Forms.ToolStripMenuItem(Shorten(nowPlaying)) { Enabled = false });

        if (!_host.UsingDemo && model.PlayerIssue == PlayerIssue.NoSupportedPlayer)
        {
            items.Add(Item("⚠ No supported player found — Get Spotify…", () => Open("https://www.spotify.com/download/windows/")));
        }

        items.Add(new Forms.ToolStripSeparator());
        var show = Check("Show Island", settings.ShowIsland, () => settings.ShowIsland = !settings.ShowIsland);
        show.ShortcutKeyDisplayString = _host.HotkeyRegistered ? "Ctrl+Alt+L" : "Ctrl+Alt+L (in use)";
        items.Add(show);
        items.Add(Check("Expand on Hover", settings.ExpandOnHover, () => settings.ExpandOnHover = !settings.ExpandOnHover));
        items.Add(Check("Hide When an App Is Fullscreen", settings.HideInFullscreen, () => settings.HideInFullscreen = !settings.HideInFullscreen));

        var size = new Forms.ToolStripMenuItem("Text Size");
        foreach (var option in new[] { TextSize.Small, TextSize.Medium, TextSize.Large })
        {
            size.DropDownItems.Add(Check(option.Label(), settings.TextSize == option, () => settings.TextSize = option));
        }
        items.Add(size);

        var position = new Forms.ToolStripMenuItem("Island Position");
        position.DropDownItems.Add(Check("Top Center", settings.Placement == IslandPlacement.Top, () => settings.Placement = IslandPlacement.Top));
        position.DropDownItems.Add(Check("Bottom Center (Above the Taskbar)", settings.Placement == IslandPlacement.Bottom, () => settings.Placement = IslandPlacement.Bottom));
        items.Add(position);

        var timing = new Forms.ToolStripMenuItem($"Lyrics Timing ({model.OffsetLabel})") { Enabled = model.Track is not null };
        timing.DropDownItems.Add(Item("Lyrics Too Early (−0.5s)", () => model.AdjustOffset(-0.5)));
        timing.DropDownItems.Add(Item("Lyrics Too Late (+0.5s)", () => model.AdjustOffset(0.5)));
        timing.DropDownItems.Add(Item("Reset", model.ResetOffset));
        items.Add(timing);

        items.Add(new Forms.ToolStripSeparator());
        var search = Item("Search Lyrics Manually…", _host.SearchLyricsManually);
        search.Enabled = model.Track is not null && !_host.UsingDemo;
        items.Add(search);
        var demo = Check("Demo Mode (No Spotify Needed)", _host.UsingDemo, () => settings.DemoMode = !settings.DemoMode);
        demo.Enabled = !_host.DemoForced;
        items.Add(demo);

        items.Add(new Forms.ToolStripSeparator());
        items.Add(Check("Start with Windows", StartWithWindows.IsEnabled, () => StartWithWindows.Set(!StartWithWindows.IsEnabled)));
        items.Add(Item("Quit Lyrics Island", _host.Quit));
    }

    private static Forms.ToolStripMenuItem Item(string text, Action action)
    {
        var item = new Forms.ToolStripMenuItem(text);
        item.Click += (_, _) => action();
        return item;
    }

    private static Forms.ToolStripMenuItem Check(string text, bool on, Action toggle)
    {
        var item = Item(text, toggle);
        item.Checked = on;
        return item;
    }

    /// <summary>Menu items get very wide with long titles; keep "Now playing" readable.</summary>
    private static string Shorten(string text) => text.Length <= 64 ? text : text[..63] + "…";

    private static void Open(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (System.ComponentModel.Win32Exception)
        {
        }
    }

    /// <summary>A white quote bubble, drawn at runtime so there's no binary asset yet.</summary>
    private static Icon CreateBubbleIcon()
    {
        const int size = 32;
        using var bitmap = new Bitmap(size, size);
        using (var g = Graphics.FromImage(bitmap))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using var bubble = new GraphicsPath();
            bubble.AddArc(3, 4, 12, 12, 180, 90);
            bubble.AddArc(17, 4, 12, 12, 270, 90);
            bubble.AddArc(17, 12, 12, 12, 0, 90);
            bubble.AddLine(14, 24, 9, 29);
            bubble.AddLine(9, 29, 10, 24);
            bubble.AddArc(3, 12, 12, 12, 90, 90);
            bubble.CloseFigure();
            g.FillPath(Brushes.White, bubble);
            using var dark = new SolidBrush(Color.FromArgb(20, 20, 20));
            g.FillEllipse(dark, 10, 12, 4, 4);
            g.FillEllipse(dark, 18, 12, 4, 4);
        }
        var handle = bitmap.GetHicon();
        try
        {
            return (Icon)Icon.FromHandle(handle).Clone();
        }
        finally
        {
            DestroyIcon(handle);
        }
    }

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DestroyIcon(nint handle);
}
