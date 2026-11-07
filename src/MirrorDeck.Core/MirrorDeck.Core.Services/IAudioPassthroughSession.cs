using System;
using System.Threading;
using System.Threading.Tasks;
using MirrorDeck.Core.Models;

namespace MirrorDeck.Core.Services;

public interface IAudioPassthroughSession : IAsyncDisposable
{
	bool IsRunning { get; }

	Task StartAsync(AudioDeviceInfo inputDevice, AudioDeviceInfo? outputDevice, AudioSettings settings, CancellationToken cancellationToken = default(CancellationToken));

	Task StopAsync(CancellationToken cancellationToken = default(CancellationToken));

	void UpdateSettings(AudioSettings settings);
}
