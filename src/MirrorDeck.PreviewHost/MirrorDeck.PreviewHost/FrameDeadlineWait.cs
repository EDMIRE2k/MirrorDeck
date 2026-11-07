using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.Win32.SafeHandles;

namespace MirrorDeck.PreviewHost;

internal sealed class FrameDeadlineWait : IDisposable
{
    private readonly EventWaitHandle? _timer;
    public FrameDeadlineWait()
    {
        nint handle = CreateWaitableTimerExW(0, null, 2, 0x1F0003);
        if (handle == 0) handle = CreateWaitableTimerExW(0, null, 0, 0x1F0003);
        if (handle == 0) return;
        _timer = new EventWaitHandle(false, EventResetMode.AutoReset);
        _timer.SafeWaitHandle = new SafeWaitHandle(handle, true);
    }
    public void Wait(long ticks, CancellationToken cancellation, WaitHandle changed)
    {
        cancellation.ThrowIfCancellationRequested();
        ticks = Math.Clamp(ticks, 1, Stopwatch.Frequency / 10);
        long due = -Math.Max(1, (long)Math.Ceiling(ticks * 10000000.0 / Stopwatch.Frequency));
        if (_timer != null && SetWaitableTimer(_timer.SafeWaitHandle, ref due, 0, 0, 0, false))
            WaitHandle.WaitAny(new[] { cancellation.WaitHandle, changed, _timer });
        else
            WaitHandle.WaitAny(new[] { cancellation.WaitHandle, changed }, Math.Max(1, (int)Math.Ceiling(ticks * 1000.0 / Stopwatch.Frequency)));
        cancellation.ThrowIfCancellationRequested();
    }
    public void Dispose() => _timer?.Dispose();
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern nint CreateWaitableTimerExW(nint attributes, string? name, uint flags, uint access);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWaitableTimer(SafeWaitHandle timer, ref long due, int period, nint completion, nint arg, bool resume);
}
