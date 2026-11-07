using System;
using System.Buffers;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;

namespace MirrorDeck.PreviewHost;

internal sealed class CapturedFrame : IDisposable
{
    public byte[] Pixels { get; }
    public int Width { get; }
    public int Height { get; }
    public int Stride { get; }
    public bool BottomUp { get; }
    public long ArrivalTicks { get; }
    public long SourceTicks { get; }
    private bool _disposed;

    public CapturedFrame(byte[] pixels, int width, int height, int stride, bool bottomUp, long arrival, long source)
    {
        Pixels = pixels; Width = width; Height = height; Stride = stride;
        BottomUp = bottomUp; ArrivalTicks = arrival; SourceTicks = source;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        ArrayPool<byte>.Shared.Return(Pixels);
    }
}

// Called under the capture queue lock. Map capture clocks to a monotonic host clock,
// preserving fractional frame rates and resetting on source restarts/discontinuities.
internal sealed class CaptureClock
{
    private double _previous = double.NaN;
    private double _origin;
    private long _hostOrigin;
    private long _previousArrival;

    public long Map(double seconds, long arrival)
    {
        if (!double.IsFinite(seconds) || seconds < 0) return arrival;
        if (!double.IsFinite(_previous) || seconds <= _previous || seconds - _previous > 1
            || arrival - _previousArrival > Stopwatch.Frequency)
        {
            _origin = seconds;
            _hostOrigin = arrival;
        }
        long mapped = _hostOrigin + (long)((seconds - _origin) * Stopwatch.Frequency);
        if (mapped > arrival)
        {
            _hostOrigin -= mapped - arrival;
            mapped = arrival;
        }
        _previous = seconds;
        _previousArrival = arrival;
        return mapped;
    }
}

// Queue ownership is explicit: enqueuing transfers ownership; taking transfers it
// to the render worker. Metadata travels with pixels even during source resize.
internal sealed class CaptureFrameQueue : IDisposable
{
    private readonly Queue<CapturedFrame> _frames = new();
    public long Dropped { get; private set; }
    public int Count => _frames.Count;

    public void Enqueue(CapturedFrame frame, int capacity)
    {
        while (_frames.Count >= Math.Clamp(capacity, 1, 8)) Drop();
        _frames.Enqueue(frame);
    }

    public bool TryTake(long now, long delay, bool latest, out CapturedFrame? frame, out long waitTicks)
    {
        frame = null;
        waitTicks = 0;
        if (_frames.Count == 0) return false;
        if (latest)
            while (_frames.Count > 1) Drop();
        else
            while (_frames.Count > 1 && now - _frames.Peek().SourceTicks > Math.Max(Stopwatch.Frequency / 5, delay * 3)) Drop();
        waitTicks = _frames.Peek().SourceTicks + delay - now;
        if (waitTicks > 0) return false;
        frame = _frames.Dequeue();
        return true;
    }

    public long NextDue(long delay) => _frames.Count == 0 ? long.MaxValue : _frames.Peek().SourceTicks + delay;
    private void Drop() { _frames.Dequeue().Dispose(); Dropped++; }
    public void Dispose() { while (_frames.Count > 0) _frames.Dequeue().Dispose(); }
}

internal sealed class FramePacingMetrics
{
    private readonly object _sync = new();
    private readonly List<double> _arrivals = new();
    private readonly List<double> _submits = new();
    private readonly List<double> _ages = new();
    private readonly List<double> _work = new();
    private readonly List<double> _displaySteps = new();
    private DisplayedFrameStatistics? _lastDisplay;
    private long _lastArrival, _lastSubmit;
    private int _captured, _submitted, _repeated;

    public void Capture(long now)
    {
        lock (_sync)
        {
            if (_lastArrival != 0) Add(_arrivals, Milliseconds(now - _lastArrival));
            _lastArrival = now; _captured++;
        }
    }

    public void Submit(long started, long completed, long source, bool repeated)
    {
        lock (_sync)
        {
            if (_lastSubmit != 0) Add(_submits, Milliseconds(started - _lastSubmit));
            _lastSubmit = started;
            Add(_ages, Milliseconds(started - source));
            Add(_work, Milliseconds(completed - started));
            _submitted++; if (repeated) _repeated++;
        }
    }

    public (string Text, double Fps) Snapshot(double seconds)
    {
        lock (_sync)
        {
            string text = $"capture={_captured / seconds:0.00}fps, submitted={_submitted / seconds:0.00}fps, redraws={_repeated}, "
                + $"arrivalMs[{Summarize(_arrivals)}], submitIntervalMs[{Summarize(_submits)}], "
                + $"frameAgeMs[{Summarize(_ages)}], renderAndPresentCpuMs[{Summarize(_work)}], "
                + $"displayRefreshSteps[{Summarize(_displaySteps)}]";
            double fps = (_submitted - _repeated) / seconds;
            _captured = _submitted = _repeated = 0;
            return (text, fps);
        }
    }

    public void Display(DisplayedFrameStatistics? statistics)
    {
        lock (_sync)
        {
            if (statistics is { } current && _lastDisplay is { } previous
                && current.Presents == previous.Presents + 1 && current.Refreshes >= previous.Refreshes)
                Add(_displaySteps, current.Refreshes - previous.Refreshes);
            _lastDisplay = statistics;
        }
    }

    private static string Summarize(List<double> values)
    {
        if (values.Count == 0) return "n/a";
        values.Sort();
        string text = $"p50={values[values.Count / 2]:0.00},p95={values[(int)((values.Count - 1) * .95)]:0.00},max={values[^1]:0.00}";
        values.Clear(); return text;
    }
    private static void Add(List<double> samples, double value) { if (samples.Count < 2048) samples.Add(value); }
    private static double Milliseconds(long ticks) => ticks * 1000.0 / Stopwatch.Frequency;
}
