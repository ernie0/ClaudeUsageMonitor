using System.Diagnostics;
using System.Drawing;

namespace ClaudeUsageMonitor;

/// <summary>
/// The animation clock of a dashboard page. Sections enter when they first show in the viewport, hover
/// eases in and out, a hovered card gets one sweep of light, numbers glide to new values and an icon can
/// spin while work is running. Everything is worked out while rendering: the host keeps its frame timer
/// running only while <see cref="Active"/> is set after a render and repaints just <see cref="Dirty"/>,
/// so a page at rest never redraws.
/// </summary>
internal sealed class Motion
{
    const float HoverMs = 160, SweepMs = 800;

    readonly Stopwatch clock = Stopwatch.StartNew();
    readonly Dictionary<string, (double Start, float Lead)> groups = [];
    readonly Dictionary<string, (float Amount, double Since)> hovers = [];
    readonly Dictionary<string, (double From, double To, double Start, float Duration)> values = [];
    double lastFrame;
    string? lastHover;
    bool settled, restarted = true, opening;

    /// <summary>False when the system asks for less motion: everything shows its end state.</summary>
    public bool Enabled = true;

    /// <summary>The action under the pointer.</summary>
    public string? Hover;

    /// <summary>The part of the page on screen, in page coordinates; sections outside it wait to enter.</summary>
    public RectangleF Viewport = new(0, 0, 100_000, 100_000);

    /// <summary>
    /// A fixed time in milliseconds instead of the clock; --screenshot uses it to capture frames. Clearing it
    /// lets the clock run on from that time, so it never goes backwards.
    /// </summary>
    public double? Time
    {
        get => fixedTime;
        set
        {
            if (value == null && fixedTime is { } t)
                offset = t - clock.Elapsed.TotalMilliseconds;
            fixedTime = value;
        }
    }

    double? fixedTime;
    double offset;

    /// <summary>Whether the last render had anything moving.</summary>
    public bool Active { get; private set; }

    /// <summary>What moved in the last render (page coordinates).</summary>
    public RectangleF Dirty { get; private set; }

    /// <summary>Where the last render drew floating things (tooltips) that are not tied to a hit area.</summary>
    public RectangleF Overlays { get; private set; }

    double Now => fixedTime ?? clock.Elapsed.TotalMilliseconds + offset;

    /// <summary>Plays the entrance again (window opened, range changed), except for the groups to keep.</summary>
    public void Restart(params string[] keep)
    {
        foreach (var group in groups.Keys.Except(keep).ToList())
            groups.Remove(group);
        values.Clear();
        settled = false;
        restarted = true;
    }

    /// <summary>Plays one group's entrance again (its content was replaced).</summary>
    public void Replay(string group) => groups.Remove(group);

    /// <summary>Jumps every entrance and number to its end; used by --screenshot.</summary>
    public void Settle() => settled = true;

    public void BeginFrame()
    {
        double now = Now;
        float step = (float)Math.Clamp((now - lastFrame) / HoverMs, 0, 1);
        lastFrame = now;
        opening = restarted;
        restarted = false;
        Active = false;
        Dirty = RectangleF.Empty;
        Overlays = RectangleF.Empty;
        if (Hover != lastHover)
        {
            if (Hover != null)
                hovers[Hover] = (hovers.GetValueOrDefault(Hover).Amount, now);
            lastHover = Hover;
        }
        foreach (var key in hovers.Keys.ToList())
        {
            var (amount, since) = hovers[key];
            float target = key == Hover ? 1 : 0;
            amount = !Enabled ? target : amount < target ? Math.Min(target, amount + step) : Math.Max(target, amount - step);
            if (amount == 0 && key != Hover)
                hovers.Remove(key);
            else
                hovers[key] = (amount, since);
        }
    }

    void Mark(RectangleF area)
    {
        Active = true;
        Dirty = Join(Dirty, area);
    }

    /// <summary>
    /// Registers a floating thing drawn this frame (a tooltip): it is repainted with whatever moves, and the
    /// host repaints it when it appears, moves or goes away.
    /// </summary>
    public void Follow(RectangleF area)
    {
        if (area.IsEmpty)
            return;
        Overlays = Join(Overlays, area);
        if (Active)
            Dirty = Join(Dirty, area);
    }

    /// <summary>Union that ignores empty rectangles.</summary>
    public static RectangleF Join(RectangleF a, RectangleF b) => a.IsEmpty ? b : b.IsEmpty ? a : RectangleF.Union(a, b);

    /// <summary>
    /// Linear entrance progress (0..1) of an element of <paramref name="group"/>, which waits
    /// <paramref name="delay"/> ms after the page entrance. A group starts when any of its elements first
    /// shows in the viewport; one that scrolls in later keeps its own cascade but skips the wait before it.
    /// </summary>
    public float In(string group, RectangleF area, float delay = 0, float duration = 600)
    {
        if (!Enabled || settled)
            return 1;
        if (!groups.TryGetValue(group, out var g))
        {
            if (!area.IntersectsWith(Viewport))
                return 0;
            groups[group] = g = (Now, opening ? 0 : delay);
            Mark(area);
        }
        double t = (Now - g.Start - (delay - g.Lead)) / duration;
        if (t >= 1)
            return 1;
        Mark(area);
        return t <= 0 ? 0 : (float)t;
    }

    /// <summary>Eased hover amount (0..1) of an action.</summary>
    public float HoverOf(string action, RectangleF area)
    {
        if (!hovers.TryGetValue(action, out var h))
            return 0;
        if (h.Amount != (action == Hover ? 1 : 0))
            Mark(area);
        return Smooth(h.Amount);
    }

    /// <summary>Progress (0..1) of the light sweeping once across a freshly hovered element, or -1.</summary>
    public float Sweep(string action, RectangleF area)
    {
        if (!Enabled || action != Hover || !hovers.TryGetValue(action, out var h))
            return -1;
        double t = (Now - h.Since) / SweepMs;
        if (t is >= 1 or < 0)
            return -1;
        Mark(area);
        return (float)t;
    }

    /// <summary>
    /// The number to show for <paramref name="key"/>: it counts up from zero the first time and glides to
    /// each new target afterwards.
    /// </summary>
    public double Value(string key, double target, RectangleF area, float duration = 900)
    {
        if (!Enabled || settled)
            return target;
        double now = Now;
        if (!values.TryGetValue(key, out var v))
            values[key] = v = (0, target, now, duration);
        else if (v.To != target)
            values[key] = v = (Current(v, now), target, now, duration);
        return Current(v, now, area);
    }

    double Current((double From, double To, double Start, float Duration) v, double now, RectangleF? area = null)
    {
        double t = (now - v.Start) / v.Duration;
        if (t >= 1)
            return v.To;
        if (area is { } a)
            Mark(a);
        return v.From + (v.To - v.From) * Out((float)Math.Max(0, t));
    }

    /// <summary>Rotation in degrees for an icon that turns while <paramref name="on"/>.</summary>
    public float Spin(bool on, RectangleF area)
    {
        if (!on || !Enabled)
            return 0;
        Mark(area);
        return (float)(Now * 0.4 % 360);
    }

    /// <summary>Ease-out cubic.</summary>
    public static float Out(float t) => 1 - (1 - t) * (1 - t) * (1 - t);

    /// <summary>Ease-out with a small overshoot, for things that pop in.</summary>
    public static float Back(float t)
    {
        const float C = 1.7f;
        float u = t - 1;
        return 1 + (C + 1) * u * u * u + C * u * u;
    }

    static float Smooth(float t) => t * t * (3 - 2 * t);
}
