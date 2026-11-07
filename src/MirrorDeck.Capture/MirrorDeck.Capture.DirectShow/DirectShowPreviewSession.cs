using System;
using System.Threading;
using System.Threading.Tasks;
using DirectShowLib;
using MirrorDeck.Core.Models;
using MirrorDeck.Core.Services;

namespace MirrorDeck.Capture.DirectShow;

public sealed class DirectShowPreviewSession : IVideoPreviewSession, IAsyncDisposable
{
	private readonly IAppLogger _logger;

	private readonly object _sync = new object();

	private IFilterGraph2? _graph;

	private ICaptureGraphBuilder2? _builder;

	private IMediaControl? _mediaControl;

	private IVideoWindow? _videoWindow;

	private IBaseFilter? _source;

	private int _lastWidth = 640;

	private int _lastHeight = 360;

	private int _resizeQueued;

	public bool IsRunning { get; private set; }

	public DirectShowPreviewSession(IAppLogger logger)
	{
		_logger = logger;
	}

	public Task StartAsync(VideoDeviceInfo device, VideoFormatInfo? format, nint previewHandle, MirrorDeckSettings settings, CancellationToken cancellationToken = default(CancellationToken))
	{
		return Task.Run(() =>
		{
			cancellationToken.ThrowIfCancellationRequested();
			lock (_sync)
			{
				StopInternal();
				DsDevice device2 = DirectShowDeviceHelpers.FindVideoDevice(device.Id) ?? throw new InvalidOperationException("Video device not found: " + device.Name);
				_graph = (IFilterGraph2)new FilterGraph();
				_builder = (ICaptureGraphBuilder2)new CaptureGraphBuilder2();
				DsError.ThrowExceptionForHR(_builder.SetFiltergraph(_graph));
				_source = DirectShowDeviceHelpers.CreateFilter(device2);
				DsError.ThrowExceptionForHR(_graph.AddFilter(_source, device.Name));
				if ((object)format != null)
				{
					DirectShowDeviceHelpers.TryApplyFormat(_source, format.Index, _logger, format.FramesPerSecond);
				}
				int num = _builder.RenderStream(PinCategory.Preview, MediaType.Video, _source, null, null);
				if (num < 0)
				{
					num = _builder.RenderStream(PinCategory.Capture, MediaType.Video, _source, null, null);
				}
				DsError.ThrowExceptionForHR(num);
				_videoWindow = (IVideoWindow)_graph;
				_videoWindow.put_Owner(previewHandle);
				_videoWindow.put_MessageDrain(IntPtr.Zero);
				_videoWindow.put_WindowStyle(WindowStyle.Child | WindowStyle.ClipSiblings | WindowStyle.ClipChildren);
				_videoWindow.SetWindowPosition(0, 0, Math.Max(1, _lastWidth), Math.Max(1, _lastHeight));
				_videoWindow.put_Visible(OABool.True);
				_mediaControl = (IMediaControl)_graph;
				DsError.ThrowExceptionForHR(_mediaControl.Run());
				IsRunning = true;
				_logger.Info($"Started video preview: {device.Name} ({format?.DisplayName ?? "auto format"})");
			}
		}, cancellationToken);
	}

	public Task StopAsync(CancellationToken cancellationToken = default(CancellationToken))
	{
		return Task.Run(() =>
		{
			cancellationToken.ThrowIfCancellationRequested();
			if (!Monitor.TryEnter(_sync, TimeSpan.FromSeconds(1.0)))
			{
				_logger.Error("Skipping DirectShow stop because the preview graph is busy.");
				return;
			}
			try
			{
				StopInternal();
			}
			finally
			{
				Monitor.Exit(_sync);
			}
		}, cancellationToken);
	}

	public void Resize(int width, int height)
	{
		Volatile.Write(ref _lastWidth, Math.Max(1, width));
		Volatile.Write(ref _lastHeight, Math.Max(1, height));
		if (Interlocked.Exchange(ref _resizeQueued, 1) != 1)
		{
			Task.Run((Action)ApplyQueuedResize);
		}
	}

	private void ApplyQueuedResize()
	{
		try
		{
			Thread.Sleep(16);
			if (!Monitor.TryEnter(_sync, TimeSpan.FromMilliseconds(250.0)))
			{
				return;
			}
			try
			{
				_videoWindow?.SetWindowPosition(0, 0, Volatile.Read(in _lastWidth), Volatile.Read(in _lastHeight));
			}
			finally
			{
				Monitor.Exit(_sync);
			}
		}
		catch (Exception exception)
		{
			_logger.Error("Queued DirectShow resize failed.", exception);
		}
		finally
		{
			Interlocked.Exchange(ref _resizeQueued, 0);
		}
	}

	public async ValueTask DisposeAsync()
	{
		await StopAsync().ConfigureAwait(continueOnCapturedContext: false);
	}

	private void StopInternal()
	{
		IsRunning = false;
		try
		{
			_mediaControl?.Stop();
		}
		catch (Exception exception)
		{
			_logger.Error("Stopping DirectShow media control failed.", exception);
		}
		try
		{
			if (_videoWindow != null)
			{
				_videoWindow.put_Visible(OABool.False);
				_videoWindow.put_MessageDrain(IntPtr.Zero);
				_videoWindow.put_Owner(IntPtr.Zero);
			}
		}
		catch (Exception exception2)
		{
			_logger.Error("Releasing DirectShow video window failed.", exception2);
		}
		DirectShowDeviceHelpers.Release(_source);
		DirectShowDeviceHelpers.Release(_mediaControl);
		DirectShowDeviceHelpers.Release(_videoWindow);
		DirectShowDeviceHelpers.Release(_builder);
		DirectShowDeviceHelpers.Release(_graph);
		_source = null;
		_mediaControl = null;
		_videoWindow = null;
		_builder = null;
		_graph = null;
	}
}
