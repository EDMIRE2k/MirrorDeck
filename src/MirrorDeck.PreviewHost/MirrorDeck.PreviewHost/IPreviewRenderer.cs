using System;
using System.Drawing;
using System.Threading;
using MirrorDeck.Core.Models;

namespace MirrorDeck.PreviewHost;

internal readonly record struct DisplayedFrameStatistics(uint Presents, uint Refreshes);

internal interface IPreviewRenderer : IDisposable
{
	string BackendName { get; }

	bool IsActive { get; }

	void Resize(Size size);

	bool WaitForPresentation(CancellationToken cancellationToken);

    bool TryGetDisplayStatistics(out DisplayedFrameStatistics statistics);

	void Render(byte[] frame, int sourceWidth, int sourceHeight, int sourceStride, bool sourceBottomUp, Size targetSize, PreviewRenderSettings settings);
}
