using System.Windows.Media;

namespace LyricsIsland.Windows;

/// <summary>
/// SwiftUI's <c>.spring(response:dampingFraction:)</c>: a unit-mass spring with
/// stiffness (2π / response)² and damping 4π · dampingFraction / response.
/// Changing <see cref="Target"/> mid-flight keeps the velocity, like SwiftUI does.
/// </summary>
public sealed class Spring(double response, double dampingFraction, double epsilon = 0.01)
{
    /// <summary>Mode changes in IslandView.swift: <c>.spring(response: 0.42, dampingFraction: 0.82)</c>.</summary>
    public static (double Response, double Damping) Mode => (0.42, 0.82);

    /// <summary>Line changes and text size in IslandView.swift: <c>.spring(response: 0.35, dampingFraction: 0.86)</c>.</summary>
    public static (double Response, double Damping) Line => (0.35, 0.86);

    private const double MaxStep = 1 / 480.0;

    public double Value { get; private set; }
    public double Velocity { get; private set; }
    public double Target { get; set; }
    public double Response { get; set; } = response;
    public double DampingFraction { get; set; } = dampingFraction;

    /// <summary>
    /// Within <c>epsilon</c> of the target and slow enough to move less than a third of that in a 60 Hz frame.
    /// Every animation frame redraws part of a layered window, so the invisible sub-pixel tail is cut off.
    /// </summary>
    public bool IsSettled => Math.Abs(Value - Target) < epsilon && Math.Abs(Velocity) < epsilon * 20;

    public void Use((double Response, double Damping) parameters) =>
        (Response, DampingFraction) = parameters;

    /// <summary>Jumps to <paramref name="value"/> with no motion.</summary>
    public void Snap(double value)
    {
        Value = Target = value;
        Velocity = 0;
    }

    /// <summary>Advances the simulation; returns false once it has settled (and snapped to the target).</summary>
    public bool Step(double dt)
    {
        if (IsSettled)
        {
            Snap(Target);
            return false;
        }
        var stiffness = Math.Pow(2 * Math.PI / Response, 2);
        var damping = 4 * Math.PI * DampingFraction / Response;
        var steps = Math.Max(1, (int)Math.Ceiling(dt / MaxStep));
        var h = dt / steps;
        for (var i = 0; i < steps; i++)
        {
            // Semi-implicit Euler: stable at these step sizes.
            Velocity += (-stiffness * (Value - Target) - damping * Velocity) * h;
            Value += Velocity * h;
        }
        return true;
    }
}

/// <summary>Something that moves for a while. <see cref="Step"/> returns false when it's done.</summary>
public interface IAnimated
{
    bool Step(double dt);
}

/// <summary>A linear 0→1 progress over a fixed duration (cross-fades).</summary>
public sealed class Tween(double duration, Action<double> apply, Action? completed = null) : IAnimated
{
    private double _elapsed;

    public bool Step(double dt)
    {
        _elapsed += dt;
        var t = Math.Min(1, _elapsed / duration);
        apply(t);
        if (t < 1) return true;
        completed?.Invoke();
        return false;
    }

    /// <summary>Jumps to the end.</summary>
    public void Finish() => Step(duration);
}

/// <summary>
/// Drives every running animation on CompositionTarget.Rendering (in step with the display), at most
/// <see cref="MaxFps"/> times a second, and only while something moves, so an idle island costs nothing.
/// <see cref="Manual"/> lets the recorder step time itself.
/// </summary>
public static class Animator
{
    /// <summary>
    /// Rendering fires at the monitor's refresh rate (180 Hz on the test PC). Every step re-presents the
    /// layered window (about 1.4 ms of CPU each), so animations step at 60 fps: every third frame at
    /// 180 Hz, every frame at 60 Hz. A DispatcherTimer was cheaper but ticks unevenly (~45 fps).
    /// </summary>
    public const double MaxFps = 60;

    private static readonly List<IAnimated> Running = [];
    private static bool _hooked;
    private static TimeSpan _lastFrame;

    /// <summary>When true, nothing runs on its own; call <see cref="StepAll"/> to advance time.</summary>
    public static bool Manual { get; set; }

    /// <summary>Frames stepped since start (for <c>--diagnose</c>).</summary>
    public static long FramesStepped { get; private set; }

    public static string Describe() =>
        $"{FramesStepped} frames stepped; running now: {(Running.Count == 0 ? "nothing" : string.Join(", ", Running.Select(r => r.GetType().Name)))}";

    public static void Start(IAnimated animation)
    {
        if (!Running.Contains(animation)) Running.Add(animation);
        if (Manual || _hooked) return;
        _hooked = true;
        _lastFrame = TimeSpan.Zero;
        CompositionTarget.Rendering += OnRendering;
    }

    public static void Stop(IAnimated animation) => Running.Remove(animation);

    public static void StepAll(double dt)
    {
        FramesStepped++;
        foreach (var animation in Running.ToArray())
        {
            if (!animation.Step(dt)) Running.Remove(animation);
        }
    }

    private static void OnRendering(object? sender, EventArgs e)
    {
        var now = ((RenderingEventArgs)e).RenderingTime;
        if (now == _lastFrame) return; // Rendering can fire more than once per frame
        // A long gap (window was hidden, first frame) counts as one 60 Hz frame.
        var elapsed = (now - _lastFrame).TotalSeconds;
        if (_lastFrame != TimeSpan.Zero && elapsed < 1 / MaxFps - 0.002) return; // skip: nothing changes, nothing redraws
        var dt = _lastFrame == TimeSpan.Zero ? 1 / MaxFps : Math.Min(elapsed, 1 / 20.0);
        _lastFrame = now;
        StepAll(dt);
        if (Running.Count == 0)
        {
            CompositionTarget.Rendering -= OnRendering;
            _hooked = false;
        }
    }
}
