using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MirrorDeck.Core.Models;

namespace MirrorDeck.Core.Services;

public sealed class CompositeVideoDeviceEnumerator : IVideoDeviceEnumerator
{
	private readonly IReadOnlyList<IVideoDeviceEnumerator> _enumerators;

	public CompositeVideoDeviceEnumerator(params IVideoDeviceEnumerator[] enumerators)
	{
		if (enumerators.Length == 0)
		{
			throw new ArgumentException("At least one video device enumerator is required.", "enumerators");
		}
		_enumerators = enumerators;
	}

	public async Task<IReadOnlyList<VideoDeviceInfo>> GetVideoDevicesAsync(CancellationToken cancellationToken = default(CancellationToken))
	{
		List<VideoDeviceInfo> devices = new List<VideoDeviceInfo>();
		foreach (IVideoDeviceEnumerator enumerator2 in _enumerators)
		{
			cancellationToken.ThrowIfCancellationRequested();
			try
			{
				List<VideoDeviceInfo> list = devices;
				list.AddRange(await enumerator2.GetVideoDevicesAsync(cancellationToken).ConfigureAwait(continueOnCapturedContext: false));
			}
			catch
			{
			}
		}
		return devices.DistinctBy((VideoDeviceInfo device) => device.Id, StringComparer.OrdinalIgnoreCase).ToArray();
	}

	public async Task<IReadOnlyList<VideoFormatInfo>> GetFormatsAsync(VideoDeviceInfo device, CancellationToken cancellationToken = default(CancellationToken))
	{
		Exception firstFailure = null;
		foreach (IVideoDeviceEnumerator enumerator2 in _enumerators)
		{
			cancellationToken.ThrowIfCancellationRequested();
			try
			{
				IReadOnlyList<VideoFormatInfo> readOnlyList = await enumerator2.GetFormatsAsync(device, cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
				if (readOnlyList.Count > 0)
				{
					return readOnlyList;
				}
			}
			catch (Exception ex) when (!(ex is OperationCanceledException))
			{
				if (ex == null)
				{
					firstFailure = ex;
				}
			}
		}
		if (firstFailure != null)
		{
			throw firstFailure;
		}
		return Array.Empty<VideoFormatInfo>();
	}
}
