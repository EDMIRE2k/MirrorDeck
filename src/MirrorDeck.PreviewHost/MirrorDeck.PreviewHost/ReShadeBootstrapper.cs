using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;

namespace MirrorDeck.PreviewHost;

internal static class ReShadeBootstrapper
{
	private static readonly string[] ProxyFileNames = new string[3] { "dxgi.dll", "d3d11.dll", "d3d10.dll" };

	private static bool _initialized;

	public static bool LocalProxyPresent { get; private set; }

	public static bool LocalProxyLoaded { get; private set; }

	public static void Initialize(Action<string> log)
	{
		if (_initialized)
		{
			return;
		}
		_initialized = true;
		EnsureDlssFeedPreset(log);
		foreach (string item in EnumerateLocalProxyPaths())
		{
			LocalProxyPresent = true;
			if (LoadLibrary(item) == IntPtr.Zero)
			{
				int lastWin32Error = Marshal.GetLastWin32Error();
				log($"Local ReShade/proxy DLL was found but could not be loaded: {item}. Win32Error={lastWin32Error} ({new Win32Exception(lastWin32Error).Message})");
				continue;
			}
			LocalProxyLoaded = true;
			log("Loaded local ReShade/proxy DLL before D3D11 startup: " + item);
			break;
		}
	}

	private static void EnsureDlssFeedPreset(Action<string> log)
	{
		string path = Path.Combine(AppContext.BaseDirectory, "ReShadePreset.ini");
		if (!File.Exists(path))
		{
			return;
		}
		try
		{
			string[] array = File.ReadAllLines(path);
			bool flag = false;
			for (int i = 0; i < array.Length; i++)
			{
				if ((array[i].StartsWith("Techniques=", StringComparison.OrdinalIgnoreCase) || array[i].StartsWith("TechniqueSorting=", StringComparison.OrdinalIgnoreCase)) && !array[i].Contains("DLSS5_Feed@DLSS5_Feed.fx", StringComparison.OrdinalIgnoreCase))
				{
					int num = array[i].IndexOf('=');
					string text = ((num >= 0) ? array[i].Substring(0, num + 1) : string.Empty);
					string value = ((num >= 0) ? array[i].Substring(num + 1).Trim() : array[i].Trim());
					array[i] = (string.IsNullOrWhiteSpace(value) ? (text + "DLSS5_Feed@DLSS5_Feed.fx") : (array[i] + ",DLSS5_Feed@DLSS5_Feed.fx"));
					flag = true;
				}
			}
			if (flag)
			{
				File.WriteAllLines(path, array);
				log("Restored DLSS 5 Feed in ReShadePreset.ini before ReShade startup.");
			}
		}
		catch (Exception ex)
		{
			log("Could not verify ReShade DLSS 5 Feed preset state: " + ex.Message);
		}
	}

	private static IEnumerable<string> EnumerateLocalProxyPaths()
	{
		string[] proxyFileNames = ProxyFileNames;
		foreach (string path in proxyFileNames)
		{
			string text = Path.Combine(AppContext.BaseDirectory, path);
			if (File.Exists(text))
			{
				yield return text;
			}
		}
	}

	[DllImport("kernel32.dll", CharSet = CharSet.Unicode, EntryPoint = "LoadLibraryW", SetLastError = true)]
	private static extern nint LoadLibrary(string lpLibFileName);
}
