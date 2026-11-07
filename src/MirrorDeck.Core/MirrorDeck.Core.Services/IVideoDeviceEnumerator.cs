using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using MirrorDeck.Core.Models;

namespace MirrorDeck.Core.Services;

public interface IVideoDeviceEnumerator
{
	Task<IReadOnlyList<VideoDeviceInfo>> GetVideoDevicesAsync(CancellationToken cancellationToken = default(CancellationToken));

	Task<IReadOnlyList<VideoFormatInfo>> GetFormatsAsync(VideoDeviceInfo device, CancellationToken cancellationToken = default(CancellationToken));
}
