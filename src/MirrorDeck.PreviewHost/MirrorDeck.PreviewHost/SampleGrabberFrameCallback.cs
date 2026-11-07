using System;
using System.Runtime.InteropServices;
using DirectShowLib;

namespace MirrorDeck.PreviewHost;

[ComVisible(true)]
internal sealed class SampleGrabberFrameCallback : ISampleGrabberCB
{
	private readonly Action<nint, int, double> _onFrameAvailable;

	public SampleGrabberFrameCallback(Action<nint, int, double> onFrameAvailable)
	{
		_onFrameAvailable = onFrameAvailable;
	}

	public int SampleCB(double SampleTime, IMediaSample pSample)
	{
		return 0;
	}

	public int BufferCB(double SampleTime, nint pBuffer, int BufferLen)
	{
		_onFrameAvailable(pBuffer, BufferLen, SampleTime);
		return 0;
	}
}
