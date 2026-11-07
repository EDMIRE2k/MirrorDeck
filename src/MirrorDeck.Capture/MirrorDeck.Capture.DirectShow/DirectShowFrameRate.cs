using System;
using System.Runtime.InteropServices;
using DirectShowLib;

namespace MirrorDeck.Capture.DirectShow;

internal static class DirectShowFrameRate
{
	public static void Apply(AMMediaType mediaType, double fps)
	{
		if (double.IsFinite(fps) && !(fps <= 0.0) && !(fps > 1000.0) && mediaType.formatPtr != IntPtr.Zero)
		{
			long avgTimePerFrame = (long)Math.Round(10000000.0 / fps);
			if (mediaType.formatType == FormatType.VideoInfo)
			{
				VideoInfoHeader? videoInfoHeader = Marshal.PtrToStructure<VideoInfoHeader>(mediaType.formatPtr);
				videoInfoHeader.AvgTimePerFrame = avgTimePerFrame;
				Marshal.StructureToPtr(videoInfoHeader, mediaType.formatPtr, fDeleteOld: false);
			}
			else if (mediaType.formatType == FormatType.VideoInfo2)
			{
				VideoInfoHeader2? videoInfoHeader2 = Marshal.PtrToStructure<VideoInfoHeader2>(mediaType.formatPtr);
				videoInfoHeader2.AvgTimePerFrame = avgTimePerFrame;
				Marshal.StructureToPtr(videoInfoHeader2, mediaType.formatPtr, fDeleteOld: false);
			}
		}
	}
}
