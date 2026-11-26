using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows.Interop;
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
/// Notification-area icon and menu, port of the status item in AppDelegate.swift. The menu is a native
/// Win32 popup menu (rounded corners and dark/light like the rest of Windows 11), built fresh each time it
/// opens so labels and check marks are current; every change goes to <see cref="IslandSettings"/>, which
/// saves itself and takes effect at once. Left or right click opens it.
/// </summary>
internal sealed partial class TrayIcon : IDisposable
{
    private readonly ITrayHost _host;
    private readonly Forms.NotifyIcon _icon;
    private readonly Icon _image;
    private readonly HwndSource _owner;
    private bool _open;

    public TrayIcon(ITrayHost host)
    {
        _host = host;
        _image = CreateBubbleIcon();
        NativeMenu.FollowSystemTheme();
        // A hidden window to own the menu (the island window never activates, so it can't).
        _owner = new HwndSource(new HwndSourceParameters("Lyrics Island menu") { Width = 0, Height = 0, WindowStyle = 0 });
        _icon = new Forms.NotifyIcon { Icon = _image, Text = "Lyrics Island", Visible = true };
        _icon.MouseUp += (_, e) =>
        {
            if (e.Button is Forms.MouseButtons.Left or Forms.MouseButtons.Right) ShowMenuAt(Forms.Cursor.Position.X, Forms.Cursor.Position.Y);
        };
    }

    /// <summary>Shows the menu at a screen point (also used by <c>--show-menu</c>).</summary>
    public void ShowMenuAt(int x, int y)
    {
        if (_open) return;
        _open = true;
        try
        {
            using var menu = Build();
            menu.Show(_owner.Handle, x, y);
        }
        finally
        {
            _open = false;
        }
    }

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
        _image.Dispose();
        _owner.Dispose();
    }

    private NativeMenu Build()
    {
        var model = _host.Model;
        var settings = _host.Settings;
        var menu = new NativeMenu();

        menu.Header("Lyrics Island");
        var nowPlaying = model.Track is { } t && model.Playback != PlaybackState.Stopped
            ? $"Now playing: {t.Title} — {t.Artist}"
            : _host.UsingDemo ? "Demo mode" : "Spotify isn't playing";
        menu.Item(Shorten(nowPlaying), null, enabled: false);
        if (!_host.UsingDemo && model.PlayerIssue == PlayerIssue.NoSupportedPlayer)
        {
            menu.Item("⚠ No supported player found — Get Spotify…", () => Open("https://www.spotify.com/download/windows/"));
        }

        menu.Separator();
        menu.Item("Show Island", () => settings.ShowIsland = !settings.ShowIsland, check: settings.ShowIsland,
            shortcut: _host.HotkeyRegistered ? "Ctrl+Alt+L" : "Ctrl+Alt+L (in use)");
        menu.Item("Expand on Hover", () => settings.ExpandOnHover = !settings.ExpandOnHover, check: settings.ExpandOnHover);
        menu.Item("Hide When an App Is Fullscreen", () => settings.HideInFullscreen = !settings.HideInFullscreen, check: settings.HideInFullscreen);
        menu.Submenu("Text Size", sub =>
        {
            foreach (var option in new[] { TextSize.Small, TextSize.Medium, TextSize.Large })
            {
                sub.Item(option.Label(), () => settings.TextSize = option, check: settings.TextSize == option);
            }
        });
        menu.Submenu("Island Position", sub =>
        {
            sub.Item("Top Center", () => settings.Placement = IslandPlacement.Top, check: settings.Placement == IslandPlacement.Top);
            sub.Item("Bottom Center (Above the Taskbar)", () => settings.Placement = IslandPlacement.Bottom, check: settings.Placement == IslandPlacement.Bottom);
        });
        menu.Submenu($"Lyrics Timing ({model.OffsetLabel})", sub =>
        {
            sub.Item("Lyrics Too Early (−0.5s)", () => model.AdjustOffset(-0.5));
            sub.Item("Lyrics Too Late (+0.5s)", () => model.AdjustOffset(0.5));
            sub.Item("Reset", model.ResetOffset);
        }, enabled: model.Track is not null);

        menu.Separator();
        menu.Item("Search Lyrics Manually…", _host.SearchLyricsManually, enabled: model.Track is not null && !_host.UsingDemo);
        menu.Item("Demo Mode (No Spotify Needed)", () => settings.DemoMode = !settings.DemoMode, enabled: !_host.DemoForced, check: _host.UsingDemo);

        menu.Separator();
        menu.Item("Start with Windows", () => StartWithWindows.Set(!StartWithWindows.IsEnabled), check: StartWithWindows.IsEnabled);
        menu.Item("Quit Lyrics Island", _host.Quit);
        return menu;
    }

    /// <summary>Menus get very wide with long titles; keep "Now playing" readable.</summary>
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
