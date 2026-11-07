using System;

namespace MirrorDeck.Core.Models;

public static class DesktopCaptureSourceIds
{
	public const string DisplayPrefix = "mirrordeck:display:";

	public const string WindowPrefix = "mirrordeck:window:";

	public const string GamePrefix = "mirrordeck:game:";

	public static bool IsDesktopSource(string id)
	{
		if (!id.StartsWith("mirrordeck:display:", StringComparison.OrdinalIgnoreCase) && !id.StartsWith("mirrordeck:window:", StringComparison.OrdinalIgnoreCase))
		{
			return id.StartsWith("mirrordeck:game:", StringComparison.OrdinalIgnoreCase);
		}
		return true;
	}

	public static CaptureSourceType GetSourceType(string id)
	{
		if (id.StartsWith("mirrordeck:display:", StringComparison.OrdinalIgnoreCase))
		{
			return CaptureSourceType.Desktop;
		}
		if (id.StartsWith("mirrordeck:window:", StringComparison.OrdinalIgnoreCase) || id.StartsWith("mirrordeck:game:", StringComparison.OrdinalIgnoreCase))
		{
			return CaptureSourceType.Window;
		}
		return CaptureSourceType.VideoDevices;
	}
}
