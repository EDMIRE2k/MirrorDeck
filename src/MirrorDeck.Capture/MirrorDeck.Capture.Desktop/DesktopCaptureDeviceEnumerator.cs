using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using MirrorDeck.Core.Models;
using MirrorDeck.Core.Services;

namespace MirrorDeck.Capture.Desktop;

public sealed class DesktopCaptureDeviceEnumerator : IVideoDeviceEnumerator
{
	private sealed record WindowInfo(nint Handle, string Title);

	private readonly record struct CaptureBounds(int Left, int Top, int Width, int Height);

	private delegate bool EnumWindowsProc(nint hWnd, nint lParam);

	private delegate bool MonitorEnumProc(nint hMonitor, nint hdcMonitor, nint lprcMonitor, nint dwData);

	private enum GetWindowCommand : uint
	{
		Owner = 4u
	}

	private enum DwmWindowAttribute
	{
		Cloaked = 14
	}

	private struct Rect
	{
		public int Left;

		public int Top;

		public int Right;

		public int Bottom;
	}

	public Task<IReadOnlyList<VideoDeviceInfo>> GetVideoDevicesAsync(CancellationToken cancellationToken = default(CancellationToken))
	{
		return Task.Run((Func<IReadOnlyList<VideoDeviceInfo>>)(() =>
		{
			cancellationToken.ThrowIfCancellationRequested();
			List<VideoDeviceInfo> devices = new List<VideoDeviceInfo>();
			int displayIndex = 1;
			EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (nint monitor, nint _, nint rect, nint _) =>
			{
				cancellationToken.ThrowIfCancellationRequested();
				Rect rect2 = Marshal.PtrToStructure<Rect>(rect);
				int value = Math.Max(1, rect2.Right - rect2.Left);
				int value2 = Math.Max(1, rect2.Bottom - rect2.Top);
				devices.Add(new VideoDeviceInfo($"{"mirrordeck:display:"}{rect2.Left},{rect2.Top},{value},{value2}", $"Display Capture: Display {displayIndex} ({value}x{value2})"));
				displayIndex++;
				return true;
			}, IntPtr.Zero);
			foreach (WindowInfo item in EnumerateWindows(cancellationToken))
			{
				devices.Add(new VideoDeviceInfo($"{"mirrordeck:window:"}{((IntPtr)item.Handle).ToInt64():X}", "Window Capture: " + item.Title));
			}
			foreach (WindowInfo item2 in EnumerateGameWindows(cancellationToken))
			{
				devices.Add(new VideoDeviceInfo($"{"mirrordeck:game:"}{((IntPtr)item2.Handle).ToInt64():X}", "Game Capture: " + item2.Title));
			}
			return devices.DistinctBy((VideoDeviceInfo device) => device.Id, StringComparer.OrdinalIgnoreCase).ToArray();
		}), cancellationToken);
	}

	public Task<IReadOnlyList<VideoFormatInfo>> GetFormatsAsync(VideoDeviceInfo device, CancellationToken cancellationToken = default(CancellationToken))
	{
		return Task.Run((Func<IReadOnlyList<VideoFormatInfo>>)(() =>
		{
			cancellationToken.ThrowIfCancellationRequested();
			if (!DesktopCaptureSourceIds.IsDesktopSource(device.Id))
			{
				return Array.Empty<VideoFormatInfo>();
			}
			CaptureBounds? captureBounds = TryGetBounds(device.Id);
			return captureBounds.HasValue ? new VideoFormatInfo[1]
			{
				new VideoFormatInfo(0, captureBounds.Value.Width, captureBounds.Value.Height, 60.0, "BGRA", "sRGB")
			} : Array.Empty<VideoFormatInfo>();
		}), cancellationToken);
	}

	private static CaptureBounds? TryGetBounds(string sourceId)
	{
		if (sourceId.StartsWith("mirrordeck:display:", StringComparison.OrdinalIgnoreCase))
		{
			string[] array = sourceId.Substring("mirrordeck:display:".Length).Split(',');
			if (array.Length == 4 && int.TryParse(array[0], out var result) && int.TryParse(array[1], out var result2) && int.TryParse(array[2], out var result3) && int.TryParse(array[3], out var result4))
			{
				return new CaptureBounds(result, result2, Math.Max(1, result3), Math.Max(1, result4));
			}
		}
		nint num = TryParseWindowHandle(sourceId);
		if (num == IntPtr.Zero || !GetWindowRect(num, out var lpRect))
		{
			return null;
		}
		int num2 = lpRect.Right - lpRect.Left;
		int num3 = lpRect.Bottom - lpRect.Top;
		if (num2 <= 0 || num3 <= 0)
		{
			return null;
		}
		return new CaptureBounds(lpRect.Left, lpRect.Top, num2, num3);
	}

	private static IEnumerable<WindowInfo> EnumerateWindows(CancellationToken cancellationToken)
	{
		List<WindowInfo> windows = new List<WindowInfo>();
		EnumWindows((nint handle, nint _) =>
		{
			cancellationToken.ThrowIfCancellationRequested();
			if (IsCandidateWindow(handle) && GetWindowRect(handle, out var _))
			{
				string windowTitle = GetWindowTitle(handle);
				if (!string.IsNullOrWhiteSpace(windowTitle))
				{
					windows.Add(new WindowInfo(handle, windowTitle));
				}
			}
			return true;
		}, IntPtr.Zero);
		return windows.OrderBy((WindowInfo window) => window.Title, StringComparer.CurrentCultureIgnoreCase);
	}

	private static IEnumerable<WindowInfo> EnumerateGameWindows(CancellationToken cancellationToken)
	{
		HashSet<nint> processWindows = new HashSet<nint>();
		Process[] processes = Process.GetProcesses();
		foreach (Process process in processes)
		{
			cancellationToken.ThrowIfCancellationRequested();
			try
			{
				if (process.MainWindowHandle != IntPtr.Zero)
				{
					processWindows.Add(process.MainWindowHandle);
				}
			}
			catch
			{
			}
			finally
			{
				process.Dispose();
			}
		}
		return from window in EnumerateWindows(cancellationToken)
			where processWindows.Contains(window.Handle)
			select window;
	}

	private static bool IsCandidateWindow(nint handle)
	{
		if (!IsWindowVisible(handle) || GetWindowTextLength(handle) == 0)
		{
			return false;
		}
		if (GetWindow(handle, GetWindowCommand.Owner) != IntPtr.Zero)
		{
			return false;
		}
		if (DwmGetWindowAttribute(handle, DwmWindowAttribute.Cloaked, out var pvAttribute, Marshal.SizeOf<int>()) == 0)
		{
			return pvAttribute == 0;
		}
		return true;
	}

	private static string GetWindowTitle(nint handle)
	{
		int windowTextLength = GetWindowTextLength(handle);
		if (windowTextLength <= 0)
		{
			return string.Empty;
		}
		char[] array = new char[windowTextLength + 1];
		int windowText = GetWindowText(handle, array, array.Length);
		if (windowText <= 0)
		{
			return string.Empty;
		}
		return new string(array, 0, windowText).Trim();
	}

	private static nint TryParseWindowHandle(string sourceId)
	{
		string text;
		if (sourceId.StartsWith("mirrordeck:window:", StringComparison.OrdinalIgnoreCase))
		{
			text = "mirrordeck:window:";
		}
		else
		{
			text = (sourceId.StartsWith("mirrordeck:game:", StringComparison.OrdinalIgnoreCase) ? "mirrordeck:game:" : null);
		}
		if (text == null)
		{
			return IntPtr.Zero;
		}
		if (!long.TryParse(sourceId.Substring(text.Length), NumberStyles.HexNumber, null, out var result))
		{
			return IntPtr.Zero;
		}
		return new IntPtr(result);
	}

	[DllImport("user32.dll")]
	private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, nint lParam);

	[DllImport("user32.dll")]
	private static extern bool EnumDisplayMonitors(nint hdc, nint lprcClip, MonitorEnumProc lpfnEnum, nint dwData);

	[DllImport("user32.dll")]
	private static extern bool IsWindowVisible(nint hWnd);

	[DllImport("user32.dll", CharSet = CharSet.Unicode)]
	private static extern int GetWindowText(nint hWnd, char[] lpString, int nMaxCount);

	[DllImport("user32.dll", CharSet = CharSet.Unicode)]
	private static extern int GetWindowTextLength(nint hWnd);

	[DllImport("user32.dll")]
	private static extern bool GetWindowRect(nint hWnd, out Rect lpRect);

	[DllImport("user32.dll")]
	private static extern nint GetWindow(nint hWnd, GetWindowCommand uCmd);

	[DllImport("dwmapi.dll")]
	private static extern int DwmGetWindowAttribute(nint hwnd, DwmWindowAttribute dwAttribute, out int pvAttribute, int cbAttribute);
}
