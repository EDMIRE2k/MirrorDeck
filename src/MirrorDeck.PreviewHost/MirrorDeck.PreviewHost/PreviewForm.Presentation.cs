using System;
using System.Buffers;
using System.Diagnostics;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using MirrorDeck.Core.Models;

namespace MirrorDeck.PreviewHost;

internal sealed partial class PreviewForm
{
    private readonly CaptureFrameQueue _frames = new();
    private readonly CaptureClock _captureClock = new();
    private readonly FramePacingMetrics _metrics = new();
    private readonly AutoResetEvent _frameReady = new(false);
    private readonly CancellationTokenSource _renderStop = new();
    private readonly TaskCompletionSource _renderStopped = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Thread? _renderThread;
    private nint _previewWindowHandle;
    private sealed record RenderSurface(Size Size, bool Visible, nint Handle);
    private RenderSurface _surface = new(new Size(1280, 720), true, 0);
    private int _frameWidth, _frameHeight, _frameStride;
    private bool _frameBottomUp, _capturingDesktopFrame, _fullscreen, _highResolutionTimerEnabled;
    private volatile bool _stopping;
    private bool _closeAllowed, _closePending;
    private string _rendererMode = "waiting";
    private long _lastTelemetryTicks = Stopwatch.GetTimestamp();
    private Thread? _desktopWorker;
    private readonly TaskCompletionSource _desktopStopped = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private bool _desktopCaptureFailed;
    private double _sourceFps = 60;
    private int _settingsRevision;
    private Bitmap? _pendingBitmap;
    private int _bitmapPosted;

    private void OnFrameAvailable(nint buffer, int length, double timestamp)
    {
        if (_stopping || _frameWidth <= 0 || _frameHeight <= 0 || length < (long)_frameStride * _frameHeight) return;
        byte[] pixels = RentFrameBuffer(length);
        try { Marshal.Copy(buffer, pixels, 0, length); }
        catch { ReturnRenderFrame(pixels); throw; }
        QueueFrame(pixels, length, _frameWidth, _frameHeight, _frameStride, _frameBottomUp, timestamp);
    }

    private void QueueFrame(byte[] pixels, int length, int width, int height, int stride, bool bottomUp, double timestamp = double.NaN)
    {
        long now = Stopwatch.GetTimestamp();
        var settings = GetRenderSettingsSnapshot();
        lock (_frameLock)
        {
            if (_stopping || length < (long)stride * height)
            {
                ReturnRenderFrame(pixels);
                return;
            }
            long source = _captureClock.Map(timestamp, now);
            _frames.Enqueue(new CapturedFrame(pixels, width, height, stride, bottomUp, now, source),
                settings.LowLatencyMode ? 1 : Math.Clamp(settings.FrameBufferCount, 1, 8));
        }
        _metrics.Capture(now);
        _frameReady.Set();
    }

    private void StartRenderWorker()
    {
        if (_renderThread != null) return;
        _previewWindowHandle = _previewPanel.Handle;
        PublishRenderSurface();
        _renderThread = new Thread(RenderLoop) { IsBackground = true, Name = "MirrorDeck presentation" };
        _renderThread.Start();
        _renderTimer.Start();
    }

    private void PublishRenderSurface()
    {
        Volatile.Write(ref _surface, new RenderSurface(_previewPanel.ClientSize, WindowState != FormWindowState.Minimized,
            _previewPanel.IsHandleCreated ? _previewPanel.Handle : 0));
        _frameReady.Set();
    }

    private void PostUi(Action action)
    {
        if (_stopping || IsDisposed || !IsHandleCreated) return;
        try { BeginInvoke(() => { if (!_stopping && !IsDisposed) action(); }); }
        catch (InvalidOperationException) { }
    }

    private void RenderLoop()
    {
        var token = _renderStop.Token;
        CapturedFrame? last = null;
        int renderedRevision = -1, previousCap = -1;
        Size renderedSize = Size.Empty;
        bool gpuFailed = false, slotReady = false;
        long nextSubmit = 0, waitStarted = 0;
        nint rendererHandle = 0;
        Size observedSize = Size.Empty, committedSize = Size.Empty;
        long sizeChangedAt = 0;
        using var deadlines = new FrameDeadlineWait();
        try
        {
            while (!token.IsCancellationRequested)
            {
                var settings = GetRenderSettingsSnapshot();
                var surface = Volatile.Read(ref _surface);
                int revision = Volatile.Read(ref _settingsRevision);
                if (!surface.Visible || surface.Handle == 0 || surface.Size.Width <= 0 || surface.Size.Height <= 0)
                {
                    nextSubmit = 0;
                    waitStarted = 0;
                    deadlines.Wait(Stopwatch.Frequency / 10, token, _frameReady);
                    continue;
                }
                if (surface.Size != observedSize)
                {
                    observedSize = surface.Size;
                    sizeChangedAt = Stopwatch.GetTimestamp();
                }
                if (committedSize.IsEmpty || Stopwatch.GetTimestamp() - sizeChangedAt >= Stopwatch.Frequency / 8)
                    committedSize = observedSize;
                if (rendererHandle != surface.Handle)
                {
                    _gpuRenderer?.Dispose();
                    _gpuRenderer = null;
                    rendererHandle = surface.Handle;
                    _previewWindowHandle = surface.Handle;
                    slotReady = false;
                    gpuFailed = false;
                    renderedRevision = -1;
                }
                int cap = Math.Clamp(settings.FpsCap, 0, 240);
                if (cap != previousCap) { nextSubmit = 0; previousCap = cap; }
                long delay = settings.LowLatencyMode ? 0 :
                    (long)((Math.Clamp(settings.FrameBufferCount, 1, 8) - 1) * Stopwatch.Frequency / Math.Max(1, _sourceFps));
                long due;
                lock (_frameLock) due = _frames.NextDue(delay);
                bool redraw = last != null && (revision != renderedRevision || committedSize != renderedSize);
                if (redraw) due = Math.Min(due, Stopwatch.GetTimestamp());
                if (due == long.MaxValue)
                {
                    deadlines.Wait(Stopwatch.Frequency / 10, token, _frameReady);
                    continue;
                }
                long remaining = Math.Max(due, nextSubmit) - Stopwatch.GetTimestamp();
                if (remaining > 0)
                {
                    deadlines.Wait(remaining, token, _frameReady);
                    continue;
                }
                if (!gpuFailed && _gpuRenderer == null)
                {
                    try
                    {
                        _gpuRenderer = CreateGpuRenderer(committedSize);
                        PostUi(() => _previewPanel.SetGpuActive());
                    }
                    catch (Exception ex)
                    {
                        gpuFailed = true;
                        _logger.Error("GPU initialization failed; using CPU fallback.", ex);
                    }
                }
                if (_gpuRenderer != null && !slotReady)
                {
                    if (!_gpuRenderer.WaitForPresentation(token))
                    {
                        if (waitStarted == 0) waitStarted = Stopwatch.GetTimestamp();
                        if (Stopwatch.GetTimestamp() - waitStarted > Stopwatch.Frequency * 3)
                            throw new TimeoutException("DXGI presentation has not become ready for three seconds.");
                        continue;
                    }
                    slotReady = true;
                    waitStarted = 0;
                }
                CapturedFrame? next;
                lock (_frameLock)
                    _frames.TryTake(Stopwatch.GetTimestamp(), delay, settings.LowLatencyMode || settings.DropLateFrames, out next, out _);
                if (next == null && !redraw) continue;
                bool repeated = next == null;
                if (next != null) { last?.Dispose(); last = next; }
                if (last == null) continue;
                long started = Stopwatch.GetTimestamp();
                try
                {
                    if (_gpuRenderer != null)
                    {
                        _gpuRenderer.Render(last.Pixels, last.Width, last.Height, last.Stride, last.BottomUp, committedSize, settings);
                        _metrics.Display(_gpuRenderer.TryGetDisplayStatistics(out var display) ? display : null);
                        _rendererMode = _gpuRenderer.BackendName + (settings.DisableProcessing ? " fast" : " shaders");
                        slotReady = false;
                    }
                    else
                    {
                        PublishCpuFrame(ProcessedFrameRenderer.Render(last.Pixels, last.Width, last.Height, last.Stride, last.BottomUp, committedSize, settings));
                        _rendererMode = settings.Scaling.Filter >= ScalingFilter.SharpBilinear
                            ? "CPU fallback (bilinear; GPU upscaler unavailable)" : "CPU fallback";
                    }
                }
                catch (Exception ex) when (!token.IsCancellationRequested)
                {
                    _logger.Error("Rendering failed.", ex);
                    if (_gpuRenderer == null) throw;
                    _gpuRenderer.Dispose();
                    _gpuRenderer = null;
                    gpuFailed = true;
                    slotReady = false;
                    renderedRevision = -1;
                    continue;
                }
                renderedRevision = revision;
                renderedSize = committedSize;
                _metrics.Submit(started, Stopwatch.GetTimestamp(), last.SourceTicks, repeated);
                if (cap > 0)
                {
                    long period = Math.Max(1, Stopwatch.Frequency / cap);
                    nextSubmit = nextSubmit == 0 ? started + period : nextSubmit + period;
                    if (nextSubmit < started) nextSubmit = started + period;
                }
                else nextSubmit = 0;
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _logger.Error("Presentation worker stopped.", ex);
            PostUi(() => ShowStatus("Preview stopped: " + ex.Message));
        }
        finally
        {
            last?.Dispose();
            try { _gpuRenderer?.Dispose(); }
            catch (Exception ex) { _logger.Error("Renderer cleanup failed.", ex); }
            _gpuRenderer = null;
            _renderStopped.TrySetResult();
        }
    }

    private void PublishCpuFrame(Bitmap bitmap)
    {
        Interlocked.Exchange(ref _pendingBitmap, bitmap)?.Dispose();
        if (Interlocked.Exchange(ref _bitmapPosted, 1) != 0) return;
        PostUi(() =>
        {
            var frame = Interlocked.Exchange(ref _pendingBitmap, null);
            Interlocked.Exchange(ref _bitmapPosted, 0);
            if (frame != null) _previewPanel.SetFrame(frame);
        });
    }

    private IPreviewRenderer CreateGpuRenderer(Size outputSize)
    {
        try { return new D3D12PreviewRenderer(_previewWindowHandle, outputSize, _logger.Info); }
        catch (Exception ex)
        {
            _logger.Error("D3D12 initialization failed; trying D3D11.", ex);
            return new D3D11PreviewRenderer(_previewWindowHandle, outputSize, _logger.Info);
        }
    }

    private void StartCompatibilityCaptureWorker()
    {
        _desktopWorker = new Thread(() =>
        {
            using var deadlines = new FrameDeadlineWait();
            using var changed = new AutoResetEvent(false);
            long period = Stopwatch.Frequency / 60;
            long due = Stopwatch.GetTimestamp();
            try
            {
                while (!_renderStop.IsCancellationRequested && !_desktopCaptureFailed)
                {
                    long remaining = due - Stopwatch.GetTimestamp();
                    if (remaining > 0) { deadlines.Wait(remaining, _renderStop.Token, changed); continue; }
                    CaptureDesktopFrame();
                    due += period;
                    long now = Stopwatch.GetTimestamp();
                    if (due < now - period) due = now + period;
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { _logger.Error("Compatibility capture stopped.", ex); }
            finally { _desktopStopped.TrySetResult(); }
        }) { IsBackground = true, Name = "MirrorDeck compatibility capture" };
        _desktopWorker.Start();
    }

    private static byte[] RentFrameBuffer(int length) => ArrayPool<byte>.Shared.Rent(length);
    private static void ReturnRenderFrame(byte[]? frame) { if (frame != null) ArrayPool<byte>.Shared.Return(frame); }

    private void LogTelemetryIfNeeded()
    {
        long now = Stopwatch.GetTimestamp();
        double seconds = (now - _lastTelemetryTicks) / (double)Stopwatch.Frequency;
        if (seconds < 2) return;
        _lastTelemetryTicks = now;
        var metrics = _metrics.Snapshot(seconds);
        long dropped;
        int queued;
        lock (_frameLock) { dropped = _frames.Dropped; queued = _frames.Count; }
        var surface = Volatile.Read(ref _surface);
        _logger.Info($"Pacing: renderer={_rendererMode}, {metrics.Text}, queue={queued}, droppedTotal={dropped}, sourceFps={_sourceFps:0.000}, panel={surface.Size.Width}x{surface.Size.Height}. Submit metrics are CPU-side, not measured display scanout.");
        UpdateFpsOverlay(GetRenderSettingsSnapshot(), metrics.Fps);
    }
}
