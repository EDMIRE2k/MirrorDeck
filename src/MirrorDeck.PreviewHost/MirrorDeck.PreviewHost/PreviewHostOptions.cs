using System;
using System.Globalization;

namespace MirrorDeck.PreviewHost;

internal sealed record PreviewHostOptions(string? DeviceId, int? FormatIndex, int Width, int Height, bool AlwaysOnTop, string? SettingsPath, int? ParentProcessId, bool CaptureCursor, double FramesPerSecond = 0.0)
{
    public bool StartFullscreen { get; init; }
	public static PreviewHostOptions Parse(string[] args)
	{
		string deviceId = null;
		string settingsPath = null;
		int? formatIndex = null;
		int width = 1280;
		int height = 720;
		bool alwaysOnTop = false;
		int? parentProcessId = null;
		bool captureCursor = false;
		double framesPerSecond = 0.0;
		for (int i = 0; i < args.Length; i++)
		{
			string text = args[i];
			string text2 = ((i + 1 < args.Length) ? args[i + 1] : null);
			int result;
			double result2;
			int result3;
			int result4;
			int result5;
			if (text == "--device" && text2 != null)
			{
				deviceId = text2;
				i++;
			}
			else if (text == "--format" && text2 != null && int.TryParse(text2, out result))
			{
				formatIndex = result;
				i++;
			}
			else if (text == "--fps" && text2 != null && double.TryParse(text2, NumberStyles.Float, CultureInfo.InvariantCulture, out result2))
			{
				framesPerSecond = ((double.IsFinite(result2) && result2 > 0.0 && result2 <= 1000.0) ? result2 : 0.0);
				i++;
			}
			else if (text == "--width" && text2 != null && int.TryParse(text2, out result3))
			{
				width = Math.Max(320, result3);
				i++;
			}
			else if (text == "--height" && text2 != null && int.TryParse(text2, out result4))
			{
				height = Math.Max(240, result4);
				i++;
			}
			else if (text == "--settings" && text2 != null)
			{
				settingsPath = text2;
				i++;
			}
			else if (text == "--parent-pid" && text2 != null && int.TryParse(text2, out result5))
			{
				parentProcessId = result5;
				i++;
			}
			else if (text == "--topmost")
			{
				alwaysOnTop = true;
			}
			else if (text == "--capture-cursor")
			{
				captureCursor = true;
			}
		}
		return new PreviewHostOptions(deviceId, formatIndex, width, height, alwaysOnTop, settingsPath, parentProcessId, captureCursor, framesPerSecond) { StartFullscreen = Array.IndexOf(args, "--fullscreen") >= 0 };
	}
}
