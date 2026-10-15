using System;

namespace Island.Core;

// Exact translation of Sources/IslandCore/Spring.swift. Frequency is angular
// frequency (rad/s), not Hz. Retargeting must preserve both state variables.
public sealed class Spring(double value, double frequency = 19, double damping = .84)
{
    public double Value = value, Target = value, Velocity;
    public double Frequency = frequency, Damping = damping;
    public bool IsSettled => Math.Abs(Value - Target) < .025 && Math.Abs(Velocity) < .1;
    public void Retarget(double target) => Target = target;
    public void Snap(double? target = null) { Target = target ?? Target; Value = Target; Velocity = 0; }
    public void Advance(double dt)
    {
        if (!double.IsFinite(dt) || dt <= 0) return;
        double w = Math.Max(.01, Frequency), z = Math.Max(0, Damping), x = Value - Target, v = Velocity;
        if (Math.Abs(z - 1) < .0001)
        {
            double decay = Math.Exp(-w * dt), b = v + w * x;
            Value = Target + (x + b * dt) * decay; Velocity = (v - w * b * dt) * decay;
        }
        else if (z < 1)
        {
            double wd = w * Math.Sqrt(1 - z * z), decay = Math.Exp(-z * w * dt);
            double c = Math.Cos(wd * dt), s = Math.Sin(wd * dt), b = (v + z * w * x) / wd;
            double displacement = x * c + b * s;
            Value = Target + decay * displacement;
            Velocity = decay * (-z * w * displacement - x * wd * s + b * wd * c);
        }
        else
        {
            double root = Math.Sqrt(z * z - 1), r1 = -w * (z - root), r2 = -w * (z + root);
            double a = (v - r2 * x) / (r1 - r2), b = x - a;
            Value = Target + a * Math.Exp(r1 * dt) + b * Math.Exp(r2 * dt);
            Velocity = a * r1 * Math.Exp(r1 * dt) + b * r2 * Math.Exp(r2 * dt);
        }
    }
}

public sealed class IslandMotion
{
    public readonly Spring Width = new(116, 20, 1.04), Height = new(0, 20, 1.04), Radius = new(0, 20, 1.04);
    public readonly Spring Reveal = new(0, 22, 1), Tab = new(1, 22, 1);
    public bool IsSettled => Width.IsSettled && Height.IsSettled && Radius.IsSettled && Reveal.IsSettled && Tab.IsSettled;
    public void SetExpanded(bool expanded, double compactWidth = 210, double availableWidth = 852, double extraHeight = 0, double expandedWidth = 348)
    {
        Width.Retarget(Math.Min(expanded ? expandedWidth : compactWidth, availableWidth));
        Height.Retarget(expanded ? 208 + extraHeight : 32); Radius.Retarget(expanded ? 38 : 16); Reveal.Retarget(expanded ? 1 : 0);
    }
    public void Hide() { Width.Retarget(116); Height.Retarget(0); Radius.Retarget(0); Reveal.Retarget(0); }
    public void Advance(double dt, bool dragging = false)
    {
        Width.Advance(dt); if (!dragging) Height.Advance(dt); Radius.Advance(dt); Reveal.Advance(dt); Tab.Advance(dt);
    }
    public void Snap() { Width.Snap(); Height.Snap(); Radius.Snap(); Reveal.Snap(); Tab.Snap(); }
}

public static class Waveform
{
    public static double Height(int bar, double time)
    {
        double phase = bar * .73, slow = .5 + .5 * Math.Sin(time * 3.1 + phase), fast = .5 + .5 * Math.Sin(time * 6.7 - phase * 1.4);
        return .14 + .86 * (.65 * slow + .35 * fast) * (.68 + .32 * Math.Cos((bar - 3) * .38));
    }
}

public sealed class HoverGate
{
    private double? entered, left;
    private bool suppressed;
    public void Suppress() => suppressed = true;
    // Returns an expansion/collapse request once; all times are monotonic.
    public bool? Sample(bool inside, bool expanded, bool pinned, double now)
    {
        if (inside)
        {
            left = null; entered ??= now;
            if (!expanded && !suppressed && now - entered >= .04) return true;
        }
        else
        {
            entered = null; suppressed = false; left ??= now;
            if (expanded && !pinned && now - left >= .18) return false;
        }
        return null;
    }
    public static bool BezelContains(double x, double y, double left, double top, double width, double scale = 1)
        => Math.Abs(x - (left + width / 2)) <= 120 * scale && y >= top && y <= top + 6 * scale;
}
