using System;
using System.Threading;
using System.Threading.Tasks;
using MirrorDeck.Core.Models;

namespace MirrorDeck.Core.Services;

public interface IVideoPreviewSession : IAsyncDisposable
{
	bool IsRunning { get; }

	Task StartAsync(VideoDeviceInfo device, VideoFormatInfo? format, nint previewHandle, MirrorDeckSettings settings, CancellationToken cancellationToken = default(CancellationToken));

	Task StopAsync(CancellationToken cancellationToken = default(CancellationToken));

	void Resize(int width, int height);
    void SendCommand(PreviewCommand command) { }
}
