using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MirrorDeck.Core.Models;
using MirrorDeck.Core.Services;
using NAudio.CoreAudioApi;

namespace MirrorDeck.Audio.Wasapi;

public sealed class WasapiAudioDeviceEnumerator : IAudioDeviceEnumerator
{
	public Task<IReadOnlyList<AudioDeviceInfo>> GetInputDevicesAsync(CancellationToken cancellationToken = default(CancellationToken))
	{
		return Task.Run(() => Enumerate(DataFlow.Capture, AudioDeviceDirection.Input, cancellationToken), cancellationToken);
	}

	public Task<IReadOnlyList<AudioDeviceInfo>> GetOutputDevicesAsync(CancellationToken cancellationToken = default(CancellationToken))
	{
		return Task.Run(() => Enumerate(DataFlow.Render, AudioDeviceDirection.Output, cancellationToken), cancellationToken);
	}

	private static IReadOnlyList<AudioDeviceInfo> Enumerate(DataFlow dataFlow, AudioDeviceDirection direction, CancellationToken cancellationToken)
	{
		using MMDeviceEnumerator mMDeviceEnumerator = new MMDeviceEnumerator();
        string? defaultId = null;
        try { using var endpoint = mMDeviceEnumerator.GetDefaultAudioEndpoint(dataFlow, Role.Multimedia); defaultId = endpoint.ID; }
        catch (System.Runtime.InteropServices.COMException) { }
		return mMDeviceEnumerator.EnumerateAudioEndPoints(dataFlow, DeviceState.Active).Select((MMDevice device) =>
		{
			cancellationToken.ThrowIfCancellationRequested();
			return new AudioDeviceInfo(device.ID, device.FriendlyName, direction);
		}).OrderBy(device => device.Id == defaultId ? 0 : 1).ThenBy((AudioDeviceInfo device) => device.Name, StringComparer.CurrentCultureIgnoreCase)
			.ToArray();
	}
}
