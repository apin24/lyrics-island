using System.Runtime.InteropServices;

namespace LyricsIsland.Windows;

/// <summary>
/// A Win32 popup menu (CreatePopupMenu / TrackPopupMenuEx). Unlike a WinForms ContextMenuStrip, which
/// draws itself, it looks like every other menu on the system: Windows 11's rounded corners and, with
/// <see cref="FollowSystemTheme"/>, dark or light like the system's app setting.
/// </summary>
internal sealed partial class NativeMenu : IDisposable
{
    private const uint MF_STRING = 0x0000, MF_GRAYED = 0x0001, MF_CHECKED = 0x0008, MF_POPUP = 0x0010, MF_SEPARATOR = 0x0800;
    private const uint TPM_RIGHTBUTTON = 0x0002, TPM_NONOTIFY = 0x0080, TPM_RETURNCMD = 0x0100;
    private const uint WM_NULL = 0x0000;

    private readonly nint _handle;
    private readonly Context _context;

    public NativeMenu() : this(new Context())
    {
    }

    private NativeMenu(Context context)
    {
        _handle = CreatePopupMenu();
        _context = context;
    }

    /// <summary>
    /// Lets menus follow the system's dark/light app setting (uxtheme ordinal 135, SetPreferredAppMode
    /// "AllowDark", Windows 10 1903+; on 1809 the same ordinal is AllowDarkModeForApp(true)). Call once at start.
    /// </summary>
    public static void FollowSystemTheme()
    {
        try
        {
            SetPreferredAppMode(1);
            FlushMenuThemes();
        }
        catch (Exception e) when (e is EntryPointNotFoundException or DllNotFoundException)
        {
            // Older Windows: light menus.
        }
    }

    public NativeMenu Item(string text, Action? action, bool enabled = true, bool check = false, string? shortcut = null)
    {
        var id = _context.Add(action);
        var flags = MF_STRING | (enabled && action is not null ? 0 : MF_GRAYED) | (check ? MF_CHECKED : 0);
        AppendMenu(_handle, flags, (nuint)id, shortcut is null ? text : $"{text}\t{shortcut}");
        return this;
    }

    /// <summary>A disabled, bold first line (the menu's default item is drawn bold).</summary>
    public NativeMenu Header(string text)
    {
        var id = _context.Add(null);
        AppendMenu(_handle, MF_STRING | MF_GRAYED, (nuint)id, text);
        SetMenuDefaultItem(_handle, (uint)id, 0);
        return this;
    }

    public NativeMenu Separator()
    {
        AppendMenu(_handle, MF_SEPARATOR, 0, null);
        return this;
    }

    public NativeMenu Submenu(string text, Action<NativeMenu> build, bool enabled = true)
    {
        var sub = new NativeMenu(_context);
        build(sub);
        AppendMenu(_handle, MF_POPUP | (enabled ? 0 : MF_GRAYED), (nuint)sub._handle, text);
        // The parent destroys its submenus.
        return this;
    }

    /// <summary>Shows the menu at a screen point and runs the chosen item's action. Blocks until it closes.</summary>
    public void Show(nint owner, int x, int y)
    {
        FollowSystemTheme(); // pick up a theme change since the last time
        // Required for a notification-area menu to close when the user clicks elsewhere.
        SetForegroundWindow(owner);
        var command = TrackPopupMenuEx(_handle, TPM_RETURNCMD | TPM_RIGHTBUTTON | TPM_NONOTIFY, x, y, owner, 0);
        PostMessage(owner, WM_NULL, 0, 0);
        if (command != 0) _context.Run(command);
    }

    public void Dispose() => DestroyMenu(_handle);

    private sealed class Context
    {
        private readonly Dictionary<int, Action> _actions = new();
        private int _next = 1;

        public int Add(Action? action)
        {
            var id = _next++;
            if (action is not null) _actions[id] = action;
            return id;
        }

        public void Run(int id)
        {
            if (_actions.TryGetValue(id, out var action)) action();
        }
    }

    [LibraryImport("user32.dll")]
    private static partial nint CreatePopupMenu();

    [LibraryImport("user32.dll", EntryPoint = "AppendMenuW", StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool AppendMenu(nint menu, uint flags, nuint idOrSubmenu, string? text);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetMenuDefaultItem(nint menu, uint item, uint byPosition);

    [LibraryImport("user32.dll")]
    private static partial int TrackPopupMenuEx(nint menu, uint flags, int x, int y, nint owner, nint parameters);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DestroyMenu(nint menu);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetForegroundWindow(nint window);

    [LibraryImport("user32.dll", EntryPoint = "PostMessageW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool PostMessage(nint window, uint message, nint wParam, nint lParam);

    [LibraryImport("uxtheme.dll", EntryPoint = "#135")]
    private static partial int SetPreferredAppMode(int mode);

    [LibraryImport("uxtheme.dll", EntryPoint = "#136")]
    private static partial void FlushMenuThemes();
}
