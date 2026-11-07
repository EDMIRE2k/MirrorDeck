using System;
using System.Collections.Generic;
using System.IO;

namespace MirrorDeck.Settings.Persistence;

public static class MirrorDeckStorage
{
	public const string PortableMarkerFileName = "MirrorDeck.portable";

	public static string GetSettingsPath()
	{
		return Path.Combine(GetStorageDirectory(), "MirrorDeck.settings.json");
	}

	public static string GetLogPath()
	{
		return Path.Combine(GetStorageDirectory(), "MirrorDeck.log");
	}

	public static string GetPreviewRenderSettingsPath()
	{
		return Path.Combine(GetStorageDirectory(), "preview-render-settings.json");
	}

	public static string GetStorageDirectory()
	{
		if (!TryGetPortableRoot(out string portableRoot))
		{
			return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MirrorDeck");
		}
		return portableRoot;
	}

	private static bool TryGetPortableRoot(out string portableRoot)
	{
		foreach (string item in EnumeratePortableRootCandidates())
		{
			if (File.Exists(Path.Combine(item, "MirrorDeck.portable")))
			{
				portableRoot = item;
				return true;
			}
		}
		portableRoot = string.Empty;
		return false;
	}

	private static IEnumerable<string> EnumeratePortableRootCandidates()
	{
		string baseDirectory = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
		if (!string.IsNullOrWhiteSpace(baseDirectory))
		{
			yield return baseDirectory;
		}
		DirectoryInfo parent = Directory.GetParent(baseDirectory);
		if (parent != null)
		{
			yield return parent.FullName;
		}
	}
}
