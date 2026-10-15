using System;

namespace Island.Core;

public sealed class TimerSession
{
    public double Duration { get; set; }
    public DateTimeOffset? Deadline { get; set; }
    public double? PausedRemaining { get; set; }
    public bool IsPaused => PausedRemaining.HasValue;
    public TimerSession() { }
    public TimerSession(double duration, DateTimeOffset now) { Duration = Math.Max(1, duration); Deadline = now.AddSeconds(Duration); }
    public double Remaining(DateTimeOffset now) => Math.Max(0, PausedRemaining ?? (Deadline - now)?.TotalSeconds ?? 0);
    public bool IsComplete(DateTimeOffset now) => !IsPaused && Remaining(now) <= 0;
    public void TogglePause(DateTimeOffset now)
    {
        if (PausedRemaining is double value) { Deadline = now.AddSeconds(value); PausedRemaining = null; }
        else { PausedRemaining = Remaining(now); Deadline = null; }
    }
    public void Extend(double seconds, DateTimeOffset now)
    {
        if (!double.IsFinite(seconds) || seconds <= 0) return;
        Duration += seconds;
        if (PausedRemaining.HasValue) PausedRemaining += seconds;
        else Deadline = (Deadline > now ? Deadline.Value : now).AddSeconds(seconds);
    }
    public static string Label(double seconds)
    {
        int value = (int)Math.Ceiling(Math.Max(0, seconds));
        return value >= 3600 ? $"{value / 3600}:{value / 60 % 60:00}:{value % 60:00}" : $"{value / 60:00}:{value % 60:00}";
    }
}
