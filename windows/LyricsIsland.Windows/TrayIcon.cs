using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using Forms = System.Windows.Forms;

namespace LyricsIsland.Windows;

/// <summary>
/// Notification-area icon. Milestone 3 only needs a way to quit and to compare the two positions;
/// the full menu from AppDelegate.swift comes in milestone 6.
/// </summary>
internal sealed partial class TrayIcon : IDisposable
{
    private readonly Forms.NotifyIcon _icon;
    private readonly Icon _image;

    public TrayIcon(Func<IslandPosition> getPosition, Action<IslandPosition> setPosition, Action quit)
    {
        _image = CreateBubbleIcon();
        var menu = new Forms.ContextMenuStrip();
        var header = new Forms.ToolStripMenuItem("Lyrics Island (preview)") { Enabled = false };
        var top = new Forms.ToolStripMenuItem("Top Center", null, (_, _) => setPosition(IslandPosition.Top));
        var bottom = new Forms.ToolStripMenuItem("Bottom Center", null, (_, _) => setPosition(IslandPosition.Bottom));
        menu.Opening += (_, _) =>
        {
            top.Checked = getPosition() == IslandPosition.Top;
            bottom.Checked = getPosition() == IslandPosition.Bottom;
        };
        menu.Items.AddRange([header, new Forms.ToolStripSeparator(), top, bottom, new Forms.ToolStripSeparator(),
            new Forms.ToolStripMenuItem("Quit", null, (_, _) => quit())]);

        _icon = new Forms.NotifyIcon { Icon = _image, Text = "Lyrics Island", ContextMenuStrip = menu, Visible = true };
    }

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
        _image.Dispose();
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
