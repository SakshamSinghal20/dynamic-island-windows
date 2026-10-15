using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Media;

namespace Island.Diagnostics;

public sealed class MotionMetrics
{
    private readonly List<double> intervals = new(16000), work = new(16000);
    private readonly List<double> renderIntervals = new(16000);
    private long allocated;
    public int Frames { get; private set; }
    public int DisconnectedShapeFrames { get; set; }
    public int HostChanges { get; set; }
    public void Record(double? interval, double? renderInterval, double cpu, long bytes)
    {
        Frames++; if (interval is > 0 && intervals.Count < 16000) intervals.Add(interval.Value * 1000);
        if (renderInterval is > 0 && renderIntervals.Count < 16000) renderIntervals.Add(renderInterval.Value * 1000);
        if (work.Count < 16000) work.Add(cpu * 1000); allocated += bytes;
    }
    public void Reset() { intervals.Clear(); renderIntervals.Clear(); work.Clear(); Frames = 0; allocated = 0; DisconnectedShapeFrames = 0; HostChanges = 0; }
    private static double Percentile(List<double> values, double p)
    { if (values.Count == 0) return 0; var sorted = values.Order().ToArray(); return sorted[(int)Math.Clamp(Math.Ceiling(p * sorted.Length) - 1, 0, sorted.Length - 1)]; }
    public object Snapshot() => new
    {
        frames = Frames, intervals = intervals.Count,
        callbackFps = intervals.Count == 0 ? 0 : 1000 / intervals.Average(),
        renderTimelineFps = renderIntervals.Count == 0 ? 0 : 1000 / renderIntervals.Average(),
        renderIntervalP95Ms = Percentile(renderIntervals, .95), renderIntervalP99Ms = Percentile(renderIntervals, .99), renderIntervalsOver25Ms = renderIntervals.Count(x => x > 25),
        intervalP50Ms = Percentile(intervals, .5), intervalP95Ms = Percentile(intervals, .95), intervalP99Ms = Percentile(intervals, .99),
        intervalMaxMs = intervals.Count == 0 ? 0 : intervals.Max(), intervalsOver25Ms = intervals.Count(x => x > 25),
        workP95Ms = Percentile(work, .95), workMaxMs = work.Count == 0 ? 0 : work.Max(),
        callbackAllocatedBytes = allocated, renderingTier = RenderCapability.Tier >> 16,
        disconnectedShapeFrames = DisconnectedShapeFrames, hostChangesDuringExercise = HostChanges,
        note = "WPF callback cadence and CPU work, not GPU presentation proof. Use PresentMon/ETW on target hardware for compositor pacing."
    };
}
