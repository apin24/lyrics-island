using Microsoft.Win32;

namespace LyricsIsland.Windows;

/// <summary>"Start with Windows": a value under HKCU\Software\Microsoft\Windows\CurrentVersion\Run (no admin rights needed).</summary>
public static class StartWithWindows
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "LyricsIsland";

    /// <summary>The command Windows runs at sign-in: this executable, quoted.</summary>
    public static string Command => $"\"{Environment.ProcessPath}\"";

    /// <summary>True when the Run value points at this executable.</summary>
    public static bool IsEnabled
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue(ValueName) is string value && string.Equals(value, Command, StringComparison.OrdinalIgnoreCase);
        }
    }

    public static void Set(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey, writable: true);
        if (enabled) key.SetValue(ValueName, Command, RegistryValueKind.String);
        else if (key.GetValue(ValueName) is not null) key.DeleteValue(ValueName);
    }
}
