using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using MirrorDeck.Core.Models;
using MirrorDeck.Core.Services;
using MirrorDeck.Settings.Persistence;

namespace MirrorDeck.App.Services;

public sealed class ExternalPreviewSession : IVideoPreviewSession, IAsyncDisposable
{
	[Flags]
	private enum SetWindowPosFlags : uint
	{
		NoMove = 2u,
		NoZOrder = 4u,
		NoActivate = 0x10u
	}

	private readonly IAppLogger _logger;

	private readonly object _sync = new object();

	private Process? _process;

	private int _lastWidth = 1280;

	private int _lastHeight = 720;

	public static string RenderSettingsPath => MirrorDeckStorage.GetPreviewRenderSettingsPath();

	public bool IsRunning
	{
		get
		{
			lock (_sync)
			{
				Process process = _process;
				return process != null && !process.HasExited;
			}
		}
	}

	public static string PreviewHostDirectory => Path.GetDirectoryName(ResolvePreviewHostPath());

	public ExternalPreviewSession(IAppLogger logger)
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
				StopInternal(killOnly: false);
				string text = ResolvePreviewHostPath();
				ProcessStartInfo processStartInfo = new ProcessStartInfo
				{
					FileName = text,
					WorkingDirectory = Path.GetDirectoryName(text),
					UseShellExecute = false
				};
				processStartInfo.ArgumentList.Add("--device");
				processStartInfo.ArgumentList.Add(device.Id);
				if ((object)format != null)
				{
					processStartInfo.ArgumentList.Add("--format");
					processStartInfo.ArgumentList.Add(format.Index.ToString());
					processStartInfo.ArgumentList.Add("--fps");
					processStartInfo.ArgumentList.Add(format.FramesPerSecond.ToString("R", CultureInfo.InvariantCulture));
				}
				processStartInfo.ArgumentList.Add("--width");
				processStartInfo.ArgumentList.Add(Math.Max(320, _lastWidth).ToString());
				processStartInfo.ArgumentList.Add("--height");
				processStartInfo.ArgumentList.Add(Math.Max(240, _lastHeight).ToString());
				processStartInfo.ArgumentList.Add("--settings");
				processStartInfo.ArgumentList.Add(RenderSettingsPath);
				processStartInfo.ArgumentList.Add("--parent-pid");
				processStartInfo.ArgumentList.Add(Environment.ProcessId.ToString());
                if (settings.StartFullscreen) processStartInfo.ArgumentList.Add("--fullscreen");
				if (settings.CaptureOverlay.CaptureMousePointer)
				{
					processStartInfo.ArgumentList.Add("--capture-cursor");
				}
				_process = Process.Start(processStartInfo) ?? throw new InvalidOperationException("MirrorDeck preview host process could not be started.");
                var launched = _process;
				_process.EnableRaisingEvents = true;
				_process.Exited += (object? _, EventArgs _) =>
				{
                    try { _logger.Info("External preview host exited with code " + launched.ExitCode + "."); }
                    catch (InvalidOperationException) { _logger.Info("External preview host exited."); }
				};
				_logger.Info($"Started external preview host pid={_process.Id} in video capture mode for {device.Name} ({format?.DisplayName ?? "auto format"}).");
			}
		}, cancellationToken);
	}

	public Task StopAsync(CancellationToken cancellationToken = default(CancellationToken))
	{
		return Task.Run(() =>
		{
			cancellationToken.ThrowIfCancellationRequested();
			lock (_sync)
			{
				StopInternal(killOnly: false);
			}
		}, cancellationToken);
	}

	public void Resize(int width, int height)
	{
		_lastWidth = Math.Max(320, width);
		_lastHeight = Math.Max(240, height);
        // The preview applies client dimensions from its live-settings snapshot;
        // changing outer bounds here would also shrink a fullscreen preview.
	}

    public void SendCommand(PreviewCommand command)
    {
        lock (_sync)
        {
            if (_process == null || _process.HasExited) return;
            _process.Refresh();
            if (_process.MainWindowHandle != 0) PostMessage(_process.MainWindowHandle, 0x804d, (nint)command, 0);
        }
    }
    [DllImport("user32.dll")]
    private static extern bool PostMessage(nint hwnd, uint message, nint wParam, nint lParam);

	public async ValueTask DisposeAsync()
	{
		await StopAsync().ConfigureAwait(continueOnCapturedContext: false);
	}

	private void StopInternal(bool killOnly)
	{
		if (_process == null)
		{
			return;
		}
		bool exited = false;
		try
		{
			if (_process.HasExited)
			{
				exited = true;
				return;
			}
			if (!killOnly)
			{
				_process.CloseMainWindow();
				if (_process.WaitForExit(3000))
				{
					exited = true;
					_logger.Info("External preview host closed.");
					return;
				}
			}
			_process.Kill(entireProcessTree: true);
			if (!_process.WaitForExit(5000))
				throw new TimeoutException("The preview host did not exit within five seconds.");
			exited = true;
			_logger.Info("External preview host killed.");
		}
		catch (Exception exception)
		{
			_logger.Error("Stopping external preview host failed.", exception);
			throw;
		}
		finally
		{
			if (exited)
			{
				_process.Dispose();
				_process = null;
			}
		}
	}

	private static string ResolvePreviewHostPath()
	{
		string[] array = new string[3]
		{
			Path.Combine(AppContext.BaseDirectory, "PreviewHost", "MirrorDeck.PreviewHost.exe"),
			Path.Combine(AppContext.BaseDirectory, "MirrorDeck.PreviewHost.exe"),
			Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "MirrorDeck.PreviewHost", "bin", "Debug", "net8.0-windows", "MirrorDeck.PreviewHost.exe"))
		};
		string[] array2 = array;
		foreach (string text in array2)
		{
			if (File.Exists(text))
			{
				return text;
			}
		}
		throw new FileNotFoundException("MirrorDeck.PreviewHost.exe was not found. Build the full solution before running MirrorDeck.", array[0]);
	}

	[DllImport("user32.dll", SetLastError = true)]
	private static extern bool SetWindowPos(nint hWnd, nint hWndInsertAfter, int x, int y, int cx, int cy, SetWindowPosFlags flags);
}
