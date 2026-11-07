using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace MirrorDeck.App.Services;

public sealed class DlssInstallerService
{
	private const string ReShadeDownloadUrl = "https://reshade.me/downloads/ReShade_Setup_6.8.0_Addon.exe";

	private const string ReShadeRuntimeEntryName = "ReShade64.dll";

	private static readonly string[] RequiredFiles = new string[7]
	{
		"dlss5-feed.addon64",
		"renodx-dlss5.addon64",
		"nvngx_dlss.dll",
		"nvngx_dlssnr.dll",
		"ReShade.ini",
		"ReShadePreset.ini",
		Path.Combine("reshade-shaders", "Shaders", "DLSS5_Feed.fx")
	};

	private readonly string _previewHostDirectory;

	public DlssInstallerService(string previewHostDirectory)
	{
		_previewHostDirectory = previewHostDirectory;
	}

	public DlssInstallStatus CheckStatus()
	{
		List<string> list = RequiredFiles.Where((string file) => !File.Exists(Path.Combine(_previewHostDirectory, file))).ToList();
		if (!File.Exists(Path.Combine(_previewHostDirectory, "dxgi.dll")))
		{
			list.Insert(0, "dxgi.dll");
		}
		if (list.Count != 0)
		{
			return new DlssInstallStatus(IsPresent: false, list.Count == 1 && list[0] == "dxgi.dll"
                ? "ReShade not installed (optional)" : "Optional ReShade components missing", list);
		}
		return new DlssInstallStatus(IsPresent: true, "ReShade is present", Array.Empty<string>());
	}

	public async Task InstallAsync(IProgress<string>? progress = null, CancellationToken cancellationToken = default(CancellationToken))
	{
		Directory.CreateDirectory(_previewHostDirectory);
		string downloadPath = Path.Combine(Path.GetTempPath(), "MirrorDeck-ReShade_Setup_6.8.0_Addon.exe");
		string runtimePath = Path.Combine(_previewHostDirectory, "dxgi.dll");
		progress?.Report("Downloading ReShade full add-on setup...");
		using (HttpClient http = new HttpClient())
		{
			using HttpResponseMessage response = await http.GetAsync("https://reshade.me/downloads/ReShade_Setup_6.8.0_Addon.exe", cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
			response.EnsureSuccessStatusCode();
			await using Stream source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
			await using FileStream destination = File.Create(downloadPath);
			await source.CopyToAsync(destination, cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
		}
		progress?.Report("Extracting ReShade64 runtime...");
		byte[] bytes = ExtractRuntime(downloadPath);
		await File.WriteAllBytesAsync(runtimePath, bytes, cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
		progress?.Report("ReShade/DLSS 5 installed.");
	}

	private static byte[] ExtractRuntime(string setupPath)
	{
		byte[] array = File.ReadAllBytes(setupPath);
		for (int i = 0; i < array.Length - 4; i++)
		{
			if (array[i] != 80 || array[i + 1] != 75 || array[i + 2] != 3 || array[i + 3] != 4)
			{
				continue;
			}
			using MemoryStream stream = new MemoryStream(array, i, array.Length - i, writable: false);
			try
			{
				using ZipArchive zipArchive = new ZipArchive(stream, ZipArchiveMode.Read);
				ZipArchiveEntry entry = zipArchive.GetEntry("ReShade64.dll");
				if (entry == null)
				{
					continue;
				}
				using Stream stream2 = entry.Open();
				using MemoryStream memoryStream = new MemoryStream();
				stream2.CopyTo(memoryStream);
				return memoryStream.ToArray();
			}
			catch (InvalidDataException)
			{
			}
		}
		throw new InvalidDataException("The downloaded ReShade setup did not contain ReShade64.dll.");
	}
}
