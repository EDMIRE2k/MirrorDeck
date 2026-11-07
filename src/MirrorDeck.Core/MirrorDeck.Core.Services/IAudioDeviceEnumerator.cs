using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using MirrorDeck.Core.Models;

namespace MirrorDeck.Core.Services;

public interface IAudioDeviceEnumerator
{
	Task<IReadOnlyList<AudioDeviceInfo>> GetInputDevicesAsync(CancellationToken cancellationToken = default(CancellationToken));

	Task<IReadOnlyList<AudioDeviceInfo>> GetOutputDevicesAsync(CancellationToken cancellationToken = default(CancellationToken));
}
