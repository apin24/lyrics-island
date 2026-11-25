using System.ComponentModel;

namespace LyricsIsland.Core.Tests;

public class IslandSettingsTests : IDisposable
{
    private readonly TempDirectory _dir = new();

    public void Dispose() => _dir.Dispose();

    [Fact]
    public void DefaultsMatchTheMacApp()
    {
        var s = new IslandSettings();

        Assert.True(s.ShowIsland);
        Assert.True(s.ExpandOnHover);
        Assert.Equal(TextSize.Medium, s.TextSize);
        Assert.False(s.DemoMode);
        Assert.False(s.HideInFullscreen);
        Assert.Empty(s.Offsets);
    }

    [Theory]
    [InlineData(TextSize.Small, 0.9, "Small")]
    [InlineData(TextSize.Medium, 1.0, "Medium")]
    [InlineData(TextSize.Large, 1.15, "Large")]
    public void TextSizes(TextSize size, double scale, string label)
    {
        Assert.Equal(scale, size.Scale());
        Assert.Equal(label, size.Label());
    }

    [Fact]
    public void OffsetsCloseToZeroAreRemoved()
    {
        var s = new IslandSettings();
        var changes = new List<string?>();
        s.PropertyChanged += (_, e) => changes.Add(e.PropertyName);

        s.SetOffset("a", 0.5);
        s.SetOffset("b", 0.04);
        s.SetOffset("a", -0.049);

        Assert.Empty(s.Offsets);
        Assert.Equal(0, s.GetOffset("a"));
        Assert.Equal(0, s.GetOffset(null));
        Assert.Equal([nameof(IslandSettings.Offsets), nameof(IslandSettings.Offsets), nameof(IslandSettings.Offsets)], changes);
    }

    [Fact]
    public void SavesAndLoadsEverything()
    {
        var path = _dir.File("sub/settings.json");
        var s = new IslandSettings
        {
            ShowIsland = false,
            ExpandOnHover = false,
            TextSize = TextSize.Large,
            DemoMode = true,
            HideInFullscreen = true,
        };
        s.SetOffset("night drive|test band|test album|179", -1.5);

        s.Save(path);
        var loaded = IslandSettings.Load(path);

        Assert.False(loaded.ShowIsland);
        Assert.False(loaded.ExpandOnHover);
        Assert.Equal(TextSize.Large, loaded.TextSize);
        Assert.True(loaded.DemoMode);
        Assert.True(loaded.HideInFullscreen);
        Assert.Equal(-1.5, loaded.GetOffset("night drive|test band|test album|179"));
        Assert.Contains("\"textSize\": \"large\"", File.ReadAllText(path));
        Assert.False(File.Exists(path + ".tmp"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("{ this is not json")]
    [InlineData("[]")]
    [InlineData("""{"textSize": "gigantic"}""")]
    public void MissingOrBrokenFilesGiveDefaults(string? content)
    {
        var path = _dir.File("settings.json");
        if (content is not null) File.WriteAllText(path, content);

        var s = IslandSettings.Load(path);

        Assert.True(s.ShowIsland);
        Assert.Equal(TextSize.Medium, s.TextSize);
    }

    [Fact]
    public void MissingKeysKeepTheirDefaults()
    {
        var path = _dir.File("settings.json");
        File.WriteAllText(path, """{"demoMode": true, "offsets": {"a": 0.5, "b": 0.01}}""");

        var s = IslandSettings.Load(path);

        Assert.True(s.DemoMode);
        Assert.True(s.ShowIsland);
        Assert.True(s.ExpandOnHover);
        Assert.Equal(0.5, s.GetOffset("a"));
        Assert.False(s.Offsets.ContainsKey("b"));
    }

    [Fact]
    public void SaveOnChangeWritesAfterEveryChange()
    {
        var path = _dir.File("settings.json");
        var s = new IslandSettings();
        s.SaveOnChange(path);

        s.TextSize = TextSize.Small;
        Assert.Equal(TextSize.Small, IslandSettings.Load(path).TextSize);

        s.SetOffset("x", 1);
        Assert.Equal(1, IslandSettings.Load(path).GetOffset("x"));
    }

    [Fact]
    public void SettingTheSameValueRaisesNothing()
    {
        var s = new IslandSettings();
        var raised = 0;
        ((INotifyPropertyChanged)s).PropertyChanged += (_, _) => raised++;

        s.ShowIsland = true;
        s.TextSize = TextSize.Medium;

        Assert.Equal(0, raised);
    }
}
