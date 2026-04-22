using System.Diagnostics;

namespace VCDV.Diagnostics;

// Rolling ring-buffer histogram for per-stage latency.
// Values stored as Stopwatch ticks; Snapshot() reports milliseconds.
//
// Writers come from a single thread per histogram (UI or capture), so the
// lock is only contended once per second when the overlay snapshots.
public sealed class LatencyHistogram
{
    private readonly long[] _samples;
    private int _count;
    private int _index;
    private readonly object _lock = new();

    public LatencyHistogram(int capacity) => _samples = new long[capacity];

    public void Record(long valueTicks)
    {
        lock (_lock)
        {
            _samples[_index] = valueTicks;
            _index = (_index + 1) % _samples.Length;
            if (_count < _samples.Length) _count++;
        }
    }

    public (double p50, double p95, double max) Snapshot()
    {
        long[] copy;
        int n;
        lock (_lock)
        {
            if (_count == 0) return (0, 0, 0);
            copy = new long[_count];
            Array.Copy(_samples, copy, _count);
            n = _count;
        }
        Array.Sort(copy);
        return (ToMs(copy[n * 50 / 100]),
                ToMs(copy[Math.Min(n - 1, n * 95 / 100)]),
                ToMs(copy[n - 1]));
    }

    private static double ToMs(long ticks) => ticks * 1000.0 / Stopwatch.Frequency;
}

// Named histograms used across the pipeline. Rolling ~2s window at 60 fps.
public static class PerfStats
{
    public static readonly LatencyHistogram CaptureCallback = new(120);  // WinRT callback incl. YUV conversion
    public static readonly LatencyHistogram PickupLatency   = new(120);  // frame ready → picked up at VBlank
    public static readonly LatencyHistogram PresentLatency  = new(120);  // D3DImage Present duration
    public static readonly LatencyHistogram TickInterval    = new(120);  // time between CompositionTarget.Rendering fires
}
