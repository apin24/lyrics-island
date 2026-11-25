using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace LyricsIsland.Core;

public enum TextSize
{
    Small,
    Medium,
    Large,
}

public static class TextSizeExtensions
{
    public static double Scale(this TextSize size) => size switch
    {
        TextSize.Small => 0.9,
        TextSize.Large => 1.15,
        _ => 1,
    };

    public static string Label(this TextSize size) => size switch
    {
        TextSize.Small => "Small",
        TextSize.Large => "Large",
        _ => "Medium",
    };
}

/// <summary>
/// User preferences, port of the Mac app's <c>Settings.swift</c>. Stored as JSON in
/// <see cref="AppPaths.SettingsFile"/>; every change raises <see cref="PropertyChanged"/>.
/// </summary>
public sealed class IslandSettings : INotifyPropertyChanged
{
    private bool _showIsland = true;
    private bool _expandOnHover = true;
    private TextSize _textSize = TextSize.Medium;
    private bool _demoMode;
    private bool _hideInFullscreen;
    private readonly Dictionary<string, double> _offsets = new(StringComparer.Ordinal);

    public event PropertyChangedEventHandler? PropertyChanged;

    public bool ShowIsland { get => _showIsland; set => Set(ref _showIsland, value); }
    public bool ExpandOnHover { get => _expandOnHover; set => Set(ref _expandOnHover, value); }
    public TextSize TextSize { get => _textSize; set => Set(ref _textSize, value); }
    public bool DemoMode { get => _demoMode; set => Set(ref _demoMode, value); }
    public bool HideInFullscreen { get => _hideInFullscreen; set => Set(ref _hideInFullscreen, value); }

    /// <summary>Per-track lyric offsets in seconds, by track ID.</summary>
    public IReadOnlyDictionary<string, double> Offsets => _offsets;

    /// <summary>Per-track lyric offset in seconds. Positive = lines appear earlier (fixes lyrics that lag behind).</summary>
    public double GetOffset(string? trackId) =>
        trackId is not null && _offsets.TryGetValue(trackId, out var value) ? value : 0;

    public void SetOffset(string trackId, double value)
    {
        if (Math.Abs(value) < 0.05) _offsets.Remove(trackId);
        else _offsets[trackId] = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Offsets)));
    }

    // ── Persistence ──────────────────────────────────────────────────────

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    /// <summary>Reads settings from <paramref name="path"/>. A missing or broken file gives the defaults.</summary>
    public static IslandSettings Load(string path)
    {
        var settings = new IslandSettings();
        SettingsFile? file = null;
        try
        {
            if (File.Exists(path)) file = JsonSerializer.Deserialize<SettingsFile>(File.ReadAllText(path), JsonOptions);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
        {
            file = null;
        }
        if (file is null) return settings;

        settings._showIsland = file.ShowIsland ?? settings._showIsland;
        settings._expandOnHover = file.ExpandOnHover ?? settings._expandOnHover;
        settings._textSize = file.TextSize is { } size && Enum.IsDefined(size) ? size : settings._textSize;
        settings._demoMode = file.DemoMode ?? settings._demoMode;
        settings._hideInFullscreen = file.HideInFullscreen ?? settings._hideInFullscreen;
        foreach (var (id, offset) in file.Offsets ?? [])
        {
            if (double.IsFinite(offset) && Math.Abs(offset) >= 0.05) settings._offsets[id] = offset;
        }
        return settings;
    }

    /// <summary>Writes the settings to <paramref name="path"/> (via a temp file, so a crash can't leave half a file).</summary>
    public void Save(string path)
    {
        var file = new SettingsFile(_showIsland, _expandOnHover, _textSize, _demoMode, _hideInFullscreen,
            new Dictionary<string, double>(_offsets, StringComparer.Ordinal));
        if (Path.GetDirectoryName(path) is { Length: > 0 } dir) Directory.CreateDirectory(dir);
        var temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(file, JsonOptions), new UTF8Encoding(false));
        File.Move(temp, path, overwrite: true);
    }

    /// <summary>Saves to <paramref name="path"/> after every change. Errors are passed to <paramref name="onError"/>.</summary>
    public void SaveOnChange(string path, Action<Exception>? onError = null)
    {
        PropertyChanged += (_, _) =>
        {
            try
            {
                Save(path);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                onError?.Invoke(e);
            }
        };
    }

    private void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    private sealed record SettingsFile(
        bool? ShowIsland,
        bool? ExpandOnHover,
        TextSize? TextSize,
        bool? DemoMode,
        bool? HideInFullscreen,
        Dictionary<string, double>? Offsets);
}
