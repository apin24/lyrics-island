using System.ComponentModel;

namespace LyricsIsland.Core.Tests;

/// <summary>The mode precedence and timing rules of the Mac app's IslandModel.swift.</summary>
public class IslandModelTests
{
    private static Track Song => ModelHarness.Song;

    // ── Mode precedence ──────────────────────────────────────────────────

    /// <summary>Hidden > Expanded > Paused > Song change > Loading > No lyrics/Mini > Lyric > Interlude.</summary>
    [Fact]
    public void ModePrecedenceFromTopToBottom()
    {
        using var h = new ModelHarness(); // lyrics stay "loading" until completed below
        h.Play(0);
        h.Pause(0);
        h.Model.ExpandNow();
        h.Settings.ShowIsland = false;
        Assert.Equal(IslandMode.Hidden, h.Model.Mode);

        h.Settings.ShowIsland = true;
        Assert.Equal(IslandMode.Expanded, h.Model.Mode);

        h.Step(0);
        h.Step(1.0); // cursor away for 1s: collapses
        Assert.Equal(IslandMode.Paused, h.Model.Mode);

        h.Play(0);
        Assert.Equal(IslandMode.SongChange, h.Model.Mode); // 1.0s after the change, lyrics still loading

        h.Run(1.5);
        Assert.Equal(IslandMode.Loading, h.Model.Mode);

        h.Lyrics.Complete(Song.Id, LyricsResult.NotFound);
        Assert.Equal(IslandMode.NoLyrics, h.Model.Mode);

        h.Run(5.1);
        Assert.Equal(IslandMode.Mini, h.Model.Mode);

        h.Model.SetManualLyrics(ModelHarness.SongLyrics, Song.Id); // position 6.6s: "second line"
        Assert.Equal(IslandMode.Lyric, h.Model.Mode);

        h.Play(9.5); // blank line
        Assert.Equal(IslandMode.Interlude, h.Model.Mode);
    }

    [Theory]
    [InlineData(LyricsOutcome.Found)]
    [InlineData(LyricsOutcome.NotFound)]
    public void SongChangeBeatsLyricsAndNoLyrics(LyricsOutcome outcome)
    {
        using var h = new ModelHarness(new LyricsResult(outcome, outcome == LyricsOutcome.Found ? ModelHarness.SongLyrics : null));
        h.Play(5); // a line is active right away

        Assert.Equal(IslandMode.SongChange, h.Model.Mode);
    }

    [Fact]
    public void HiddenWhenNothingIsPlaying()
    {
        using var h = new ModelHarness(ModelHarness.Found);

        Assert.True(h.Player.Started);
        Assert.Equal(IslandMode.Hidden, h.Model.Mode);

        h.Player.Emit(null, PlaybackState.Stopped, 0);
        Assert.Equal(IslandMode.Hidden, h.Model.Mode);
    }

    [Fact]
    public void HiddenWhenTurnedOff()
    {
        using var h = new ModelHarness(ModelHarness.Found);
        h.Play(4);
        h.Run(3);
        Assert.Equal(IslandMode.Lyric, h.Model.Mode);

        h.Settings.ShowIsland = false;

        Assert.Equal(IslandMode.Hidden, h.Model.Mode);
    }

    [Fact]
    public void HiddenInFullscreenOnlyWhenThatSettingIsOn()
    {
        using var h = new ModelHarness(ModelHarness.Found);
        h.Play(4);
        h.Run(3);

        h.Model.IsFullscreen = true;
        Assert.Equal(IslandMode.Lyric, h.Model.Mode);

        h.Settings.HideInFullscreen = true;
        Assert.Equal(IslandMode.Hidden, h.Model.Mode);

        h.Model.IsFullscreen = false;
        Assert.Equal(IslandMode.Lyric, h.Model.Mode);
    }

    [Fact]
    public void SongChangeForTwoAndAHalfSecondsThenLoadingThenLyrics()
    {
        using var h = new ModelHarness();
        h.Play(0);
        Assert.Equal(IslandMode.SongChange, h.Model.Mode);
        Assert.Equal([Song.Id], h.Lyrics.Requested);

        h.Run(2.4);
        Assert.Equal(IslandMode.SongChange, h.Model.Mode);

        h.Run(0.2);
        Assert.Equal(IslandMode.Loading, h.Model.Mode);
        Assert.Equal(LyricsState.Loading, h.Model.LyricsStatus.State);

        h.Lyrics.Complete(Song.Id, ModelHarness.Found);
        Assert.Equal(IslandMode.Interlude, h.Model.Mode); // 2.6s: before the first line at 3s

        h.Run(0.5);
        Assert.Equal(IslandMode.Lyric, h.Model.Mode);
        Assert.Equal("first line here", h.Model.CurrentLine?.Text);
    }

    [Fact]
    public void InterludeBeforeTheFirstLineAndOnBlankLines()
    {
        using var h = new ModelHarness(ModelHarness.Found);
        h.Play(0);
        h.Run(2.6);
        Assert.Equal(IslandMode.Interlude, h.Model.Mode);
        Assert.Null(h.Model.LineIndex);
        Assert.Null(h.Model.CurrentLine);

        h.Play(9.5); // blank line at 9s
        Assert.Equal(IslandMode.Interlude, h.Model.Mode);
        Assert.Equal(2, h.Model.LineIndex);
        Assert.Null(h.Model.CurrentLine);

        h.Play(13.5);
        Assert.Equal(IslandMode.Lyric, h.Model.Mode);
        Assert.Equal("after the break", h.Model.CurrentLine?.Text);
    }

    [Fact]
    public void PausedThenHiddenAfterTenSeconds()
    {
        using var h = new ModelHarness(ModelHarness.Found);
        h.Play(4);
        h.Run(3);

        h.Pause(7);
        Assert.Equal(IslandMode.Paused, h.Model.Mode);
        h.Run(10);
        Assert.Equal(IslandMode.Paused, h.Model.Mode); // exactly 10s: not yet
        h.Run(0.1);
        Assert.Equal(IslandMode.Hidden, h.Model.Mode);

        h.Play(7);
        Assert.Equal(IslandMode.Lyric, h.Model.Mode);
    }

    [Fact]
    public void PausedTakesPrecedenceOverSongChange()
    {
        using var h = new ModelHarness(ModelHarness.Found);
        h.Pause(0);

        Assert.Equal(IslandMode.Paused, h.Model.Mode);
    }

    [Theory]
    [InlineData(LyricsOutcome.NotFound)]
    [InlineData(LyricsOutcome.Failed)]
    public void NoLyricsThenMiniAfterFiveSeconds(LyricsOutcome outcome)
    {
        using var h = new ModelHarness(new LyricsResult(outcome));
        h.Play(0);
        h.Run(2.6);
        Assert.Equal(IslandMode.NoLyrics, h.Model.Mode);

        h.Run(2.4); // 5.0s since "not found"
        Assert.Equal(IslandMode.NoLyrics, h.Model.Mode);

        h.Run(0.1);
        Assert.Equal(IslandMode.Mini, h.Model.Mode);
    }

    [Fact]
    public void ManualLyricsReplaceNoLyrics()
    {
        using var h = new ModelHarness(LyricsResult.NotFound);
        h.Play(1);
        h.Run(6);
        Assert.Equal(IslandMode.Mini, h.Model.Mode);

        h.Model.SetManualLyrics(ModelHarness.SongLyrics, "some-other-track");
        Assert.Equal(IslandMode.Mini, h.Model.Mode);

        h.Model.SetManualLyrics(ModelHarness.SongLyrics, Song.Id);
        Assert.Equal(IslandMode.Lyric, h.Model.Mode);
        Assert.Equal("second line", h.Model.CurrentLine?.Text);
    }

    [Fact]
    public void ExpandedBeatsPausedButNotHidden()
    {
        using var h = new ModelHarness(ModelHarness.Found);
        h.Play(4);
        h.Run(3);
        h.Pause(7);

        h.Model.ExpandNow();
        Assert.Equal(IslandMode.Expanded, h.Model.Mode);

        h.Settings.ShowIsland = false;
        Assert.Equal(IslandMode.Hidden, h.Model.Mode);
    }

    [Fact]
    public void LyricsForAnOldTrackAreIgnored()
    {
        using var h = new ModelHarness();
        h.Play(0);
        h.Play(0, ModelHarness.OtherSong);

        h.Lyrics.Complete(Song.Id, ModelHarness.Found);

        Assert.Equal(LyricsState.Loading, h.Model.LyricsStatus.State);
        Assert.Equal(ModelHarness.OtherSong, h.Model.Track);
    }

    [Fact]
    public void RaisesModeChanges()
    {
        using var h = new ModelHarness(ModelHarness.Found);
        var modes = new List<IslandMode>();
        h.Model.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(IslandModel.Mode)) modes.Add(h.Model.Mode);
        };

        h.Play(0);
        h.Run(4);

        Assert.Equal([IslandMode.SongChange, IslandMode.Interlude, IslandMode.Lyric], modes);
    }

    // ── Timing ───────────────────────────────────────────────────────────

    [Fact]
    public void CurrentLineIsTheLastLineAtOrBeforeThePosition()
    {
        // Threshold 0 so the 10 ms jump below isn't smoothed away.
        using var h = new ModelHarness(ModelHarness.Found, resyncThreshold: 0);
        h.Play(5.99);
        Assert.Equal(0, h.Model.LineIndex);

        h.Play(6.0);
        Assert.Equal(1, h.Model.LineIndex);

        h.Play(100);
        Assert.Equal(3, h.Model.LineIndex);
    }

    [Fact]
    public void PositiveOffsetShowsLinesEarlier()
    {
        using var h = new ModelHarness(ModelHarness.Found);
        h.Play(2.6);
        Assert.Null(h.Model.LineIndex);

        h.Model.AdjustOffset(+0.5);

        Assert.Equal(0, h.Model.LineIndex);
        Assert.Equal(0.5, h.Settings.GetOffset(Song.Id));
    }

    [Fact]
    public void OffsetIsPerSongRoundedAndLabelled()
    {
        using var h = new ModelHarness(ModelHarness.Found);
        h.Play(4);
        Assert.Equal("±0.0s", h.Model.OffsetLabel);

        h.Model.AdjustOffset(+0.5);
        Assert.Equal("+0.5s", h.Model.OffsetLabel);

        h.Model.AdjustOffset(-0.5);
        h.Model.AdjustOffset(-0.5);
        Assert.Equal("-0.5s", h.Model.OffsetLabel);

        h.Settings.SetOffset(Song.Id, 0.33);
        h.Model.AdjustOffset(0.5);
        Assert.Equal(0.8, h.Settings.GetOffset(Song.Id), 10);

        h.Play(0, ModelHarness.OtherSong);
        Assert.Equal("±0.0s", h.Model.OffsetLabel);

        h.Play(4);
        h.Model.ResetOffset();
        Assert.Equal("±0.0s", h.Model.OffsetLabel);
        Assert.False(h.Settings.Offsets.ContainsKey(Song.Id));
    }

    [Fact]
    public void TimeIsClampedToTheDuration()
    {
        using var h = new ModelHarness(ModelHarness.Found);
        h.Play(19.5);
        h.Run(2);

        Assert.Equal(20, h.Model.CurrentTime);
    }

    [Fact]
    public void KaraokeProgressFollowsTheEstimate()
    {
        using var h = new ModelHarness(ModelHarness.Found);
        // Line 0, "first line here": 15 characters, 3s until the next line.
        // estimate = min(3 × 0.9, max(1.2, 15 × 0.085)) = 1.275s
        h.Play(3 + 1.275 / 2);
        Assert.Equal(0.5, h.Model.LineProgress(0), 6);

        h.Play(2);
        Assert.Equal(0, h.Model.LineProgress(0));
        h.Play(5);
        Assert.Equal(1, h.Model.LineProgress(0));

        // The last line runs until the end of the track: gap 7s, so the text decides again (1.275s).
        h.Play(13.51);
        Assert.Equal(0.51 / 1.275, h.Model.LineProgress(3), 6);

        Assert.Equal(0, h.Model.LineProgress(-1));
        Assert.Equal(0, h.Model.LineProgress(99));
    }

    [Fact]
    public void KaraokeProgressUsesTheGapForFastLines()
    {
        using var h = new ModelHarness(LyricsResult.Found(new Lyrics(LrcParser.Parse("""
            [00:01.00]a very long line that is sung quickly
            [00:02.00]next
            """), "Test")));
        // 37 characters × 0.085 = 3.145s, but only 1s until the next line: estimate = 0.9s.
        h.Play(1.45);

        Assert.Equal(0.5, h.Model.LineProgress(0), 6);
    }

    [Fact]
    public void LastLineOfATrackWithoutDurationAssumesHalfASecond()
    {
        using var h = new ModelHarness(LyricsResult.Found(new Lyrics(LrcParser.Parse("[00:01.00]only line"), "Test")));
        h.Play(1.225, new Track("no-duration", "x", "y", "z", 0));

        // gap = max(0.5, 0 - 1) = 0.5 → estimate = min(0.45, max(1.2, …)) = 0.45
        Assert.Equal(0.5, h.Model.LineProgress(0), 6);
    }

    // ── Clock resync ─────────────────────────────────────────────────────

    [Fact]
    public void SmallDriftKeepsTheLocalClock()
    {
        using var h = new ModelHarness(ModelHarness.Found);
        h.Play(10);
        h.Play(10.3);

        Assert.Equal(10, h.Model.CurrentTime, 6);
    }

    [Fact]
    public void BigDriftTakesTheReport()
    {
        using var h = new ModelHarness(ModelHarness.Found);
        h.Play(10);
        h.Play(10.4);

        Assert.Equal(10.4, h.Model.CurrentTime, 6);
    }

    [Fact]
    public void ZeroThresholdTakesEveryReport()
    {
        using var h = new ModelHarness(ModelHarness.Found, resyncThreshold: 0);
        h.Play(10);
        h.Play(10.12);

        Assert.Equal(10.12, h.Model.CurrentTime, 6);
    }

    [Fact]
    public void StateAndTrackChangesAlwaysTakeTheReport()
    {
        using var h = new ModelHarness(ModelHarness.Found);
        h.Play(10);
        h.Pause(10.1);
        Assert.Equal(10.1, h.Model.CurrentTime, 6);

        h.Play(10.2);
        Assert.Equal(10.2, h.Model.CurrentTime, 6);

        h.Play(10.3, ModelHarness.OtherSong);
        Assert.Equal(10.3, h.Model.CurrentTime, 6);
    }

    [Fact]
    public void ClockRunsBetweenReports()
    {
        using var h = new ModelHarness(ModelHarness.Found);
        h.Play(10);
        h.Run(1.5);

        Assert.Equal(11.5, h.Model.CurrentTime, 6);

        h.Pause(11.5);
        h.Run(3);
        Assert.Equal(11.5, h.Model.CurrentTime, 6);
    }

    // ── Hover ────────────────────────────────────────────────────────────

    [Fact]
    public void ExpandsAfterHoveringForThreeTenthsOfASecond()
    {
        using var h = new ModelHarness(ModelHarness.Found);
        h.Play(4);
        h.Run(3);

        h.Step(0, cursorOverPill: true);
        Assert.True(h.Model.AcceptsMouse);
        h.Step(0.29, cursorOverPill: true);
        Assert.False(h.Model.IsExpanded);
        h.Step(0.01, cursorOverPill: true);
        Assert.True(h.Model.IsExpanded);
        Assert.Equal(IslandMode.Expanded, h.Model.Mode);
    }

    [Fact]
    public void CollapsesOneSecondAfterTheCursorLeaves()
    {
        using var h = new ModelHarness(ModelHarness.Found);
        h.Play(4);
        h.Model.ExpandNow();

        h.Step(0, cursorOverPill: false);
        Assert.False(h.Model.AcceptsMouse);
        h.Step(0.99);
        Assert.True(h.Model.IsExpanded);
        h.Step(0.01);
        Assert.False(h.Model.IsExpanded);
    }

    [Fact]
    public void ComingBackCancelsTheCollapse()
    {
        using var h = new ModelHarness(ModelHarness.Found);
        h.Play(4);
        h.Model.ExpandNow();

        h.Step(0);
        h.Step(0.8);
        h.Step(0.1, cursorOverPill: true);
        h.Step(0.8);
        Assert.True(h.Model.IsExpanded);

        h.Step(1.0);
        Assert.False(h.Model.IsExpanded);
    }

    [Fact]
    public void HoverDoesNothingWhenExpandOnHoverIsOff()
    {
        using var h = new ModelHarness(ModelHarness.Found);
        h.Settings.ExpandOnHover = false;
        h.Play(4);
        h.Run(3);

        h.Run(2, cursorOverPill: true);
        Assert.False(h.Model.IsExpanded);
        Assert.True(h.Model.AcceptsMouse); // still clickable

        h.Model.ExpandNow();
        Assert.True(h.Model.IsExpanded);
    }

    [Fact]
    public void HiddenIslandIsClickThrough()
    {
        using var h = new ModelHarness(ModelHarness.Found);

        h.Run(1, cursorOverPill: true);

        Assert.False(h.Model.AcceptsMouse);
        Assert.False(h.Model.IsExpanded);
    }

    [Fact]
    public void SimulatedHoverOverridesTheCursor()
    {
        using var h = new ModelHarness(ModelHarness.Found);
        h.Play(4);
        h.Model.SimulatedHover = true;

        h.Run(0.5, cursorOverPill: false);

        Assert.True(h.Model.IsExpanded);
    }

    // ── Actions ──────────────────────────────────────────────────────────

    [Fact]
    public void SeeksByFractionAndByLine()
    {
        using var h = new ModelHarness(ModelHarness.Found);
        h.Play(4);

        h.Model.Seek(0.5);
        h.Model.Seek(2);
        h.Model.Seek(-1);
        h.Model.AdjustOffset(0.5);
        h.Model.SeekToLine(1); // 6.0 - 0.5 + 0.05
        h.Model.SeekToLine(42);

        Assert.Equal([10.0, 20.0, 0.0, 5.55], h.Player.Seeks.Select(s => Math.Round(s, 6)));
    }

    [Fact]
    public void ForwardsPlayerControlsAndIssues()
    {
        using var h = new ModelHarness(ModelHarness.Found);

        h.Model.TogglePlay();
        h.Model.Next();
        h.Model.Previous();
        h.Model.OpenPlayer();
        h.Player.RaiseIssue(PlayerIssue.NoSupportedPlayer);

        Assert.Equal(["playpause", "next", "previous", "activate"], h.Player.Commands);
        Assert.Equal(PlayerIssue.NoSupportedPlayer, h.Model.PlayerIssue);
        Assert.Equal("Fake", h.Model.PlayerName);
        Assert.True(h.Model.CanSeek);
    }

    [Fact]
    public void AttachingAnotherPlayerStopsTheOldOne()
    {
        using var h = new ModelHarness(ModelHarness.Found);
        h.Play(4);
        var second = new FakePlayer(h.Time);

        h.Model.Attach(second);
        h.Player.Emit(Song, PlaybackState.Playing, 8); // old player: ignored

        Assert.False(h.Player.Started);
        Assert.True(second.Started);
        Assert.Null(h.Model.Track);
        Assert.Equal(IslandMode.Hidden, h.Model.Mode);
    }

    [Fact]
    public void PreviewFreezesTheDemoSong()
    {
        using var h = new ModelHarness();

        h.Model.LoadPreview(12);
        h.Run(5);

        Assert.Equal(IslandMode.Lyric, h.Model.Mode);
        Assert.Equal("the city lights are fading slow", h.Model.CurrentLine?.Text);
        Assert.Equal(12, h.Model.CurrentTime);
    }

    [Fact]
    public void ForcedModeWins()
    {
        using var h = new ModelHarness();
        h.Model.ForcedMode = IslandMode.Mini;

        Assert.Equal(IslandMode.Mini, h.Model.Mode);
    }

    [Fact]
    public void WorksWithTheDemoPlayerAndRealLyricsService()
    {
        using var context = InlineSynchronizationContext.Install();
        using var cache = new TempDirectory();
        var time = new Microsoft.Extensions.Time.Testing.FakeTimeProvider();
        using var service = new LyricsService(cache.Path, new FakeHttpHandler(_ => throw new InvalidOperationException("no network")));
        var model = new IslandModel(new IslandSettings(), service, time);
        using var demo = new DemoPlayer(time: time);

        model.Attach(demo);
        for (var i = 0; i < 60; i++)
        {
            time.Advance(TimeSpan.FromMilliseconds(100));
            model.Tick(false);
        }

        Assert.Equal(IslandMode.Lyric, model.Mode);
        Assert.Equal("the road back home is long tonight", model.CurrentLine?.Text);
        Assert.Equal("Demo", model.LyricsStatus.Lyrics?.Source);
    }
}
