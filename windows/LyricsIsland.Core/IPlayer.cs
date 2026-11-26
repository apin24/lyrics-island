namespace LyricsIsland.Core;

/// <summary>
/// Something that plays music and reports what it's doing (Spotify through the Windows media controls,
/// or the demo player). Raise <see cref="Updated"/> and <see cref="IssueChanged"/> on the thread the
/// <see cref="IslandModel"/> lives on (the UI thread).
/// </summary>
public interface IPlayer
{
    event Action<PlayerSnapshot>? Updated;
    event Action<PlayerIssue?>? IssueChanged;

    /// <summary>Shown on the source chip in the expanded panel, e.g. "Spotify".</summary>
    string Name { get; }

    /// <summary>False hides click-to-seek instead of showing a dead control.</summary>
    bool CanSeek => true;

    /// <summary>
    /// How far a new report may drift from the model's running clock before the model takes it.
    /// The Mac's 0.35s suits coarse, polled positions. A player whose reports are exact and
    /// timestamped (the media controls) returns 0, so every report is taken.
    /// </summary>
    double ResyncThreshold => IslandModel.DefaultResyncThreshold;

    void Start();
    void Stop();
    void PlayPause();
    void Next();
    void Previous();
    void Seek(double seconds);

    /// <summary>Brings the player app to the front.</summary>
    void Activate();
}
