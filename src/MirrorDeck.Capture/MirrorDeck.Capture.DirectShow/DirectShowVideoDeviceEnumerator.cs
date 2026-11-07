using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using DirectShowLib;
using MirrorDeck.Core.Models;
using MirrorDeck.Core.Services;

namespace MirrorDeck.Capture.DirectShow;

public sealed class DirectShowVideoDeviceEnumerator : IVideoDeviceEnumerator
{
	public Task<IReadOnlyList<VideoDeviceInfo>> GetVideoDevicesAsync(CancellationToken cancellationToken = default(CancellationToken))
	{
		return Task.Run((Func<IReadOnlyList<VideoDeviceInfo>>)(() =>
		{
			cancellationToken.ThrowIfCancellationRequested();
			return (from device in DsDevice.GetDevicesOfCat(FilterCategory.VideoInputDevice)
				select new VideoDeviceInfo(device.DevicePath ?? device.Name, device.Name)).OrderBy((VideoDeviceInfo device) => device.Name, StringComparer.CurrentCultureIgnoreCase).ToArray();
		}), cancellationToken);
	}

	public Task<IReadOnlyList<VideoFormatInfo>> GetFormatsAsync(VideoDeviceInfo device, CancellationToken cancellationToken = default(CancellationToken))
	{
		return Task.Run((Func<IReadOnlyList<VideoFormatInfo>>)(() =>
		{
			cancellationToken.ThrowIfCancellationRequested();
			DsDevice device2 = DirectShowDeviceHelpers.FindVideoDevice(device.Id) ?? throw new InvalidOperationException("Video device not found: " + device.Name);
			IBaseFilter baseFilter = null;
			IPin pin = null;
			try
			{
				baseFilter = DirectShowDeviceHelpers.CreateFilter(device2);
				pin = DirectShowDeviceHelpers.FindCaptureOrPreviewPin(baseFilter) ?? throw new InvalidOperationException("No capture pin was found on " + device.Name + ".");
				if (pin is IAMStreamConfig iAMStreamConfig)
				{
					DsError.ThrowExceptionForHR(iAMStreamConfig.GetNumberOfCapabilities(out var piCount, out var piSize));
					nint num = Marshal.AllocCoTaskMem(piSize);
					try
					{
						List<VideoFormatInfo> list = new List<VideoFormatInfo>();
						for (int i = 0; i < piCount; i++)
						{
							cancellationToken.ThrowIfCancellationRequested();
							AMMediaType ppmt = null;
							try
							{
								DsError.ThrowExceptionForHR(iAMStreamConfig.GetStreamCaps(i, out ppmt, num));
								VideoFormatInfo videoFormatInfo = TryParseFormat(i, ppmt);
								if ((object)videoFormatInfo != null)
								{
									list.Add(videoFormatInfo);
									foreach (double frameRate in GetFrameRates(baseFilter, pin, i, videoFormatInfo, num, piSize))
									{
										list.Add(videoFormatInfo with
										{
											FramesPerSecond = frameRate
										});
									}
								}
							}
							finally
							{
								if (ppmt != null)
								{
									DsUtils.FreeAMMediaType(ppmt);
								}
							}
						}
						return (from format in list.DistinctBy((VideoFormatInfo format) => new
							{
								Width = format.Width,
								Height = format.Height,
								Fps = Math.Round(format.FramesPerSecond, 3),
								PixelFormat = format.PixelFormat
							})
							orderby format.Width * format.Height descending, format.FramesPerSecond descending
							select format).ToArray();
					}
					finally
					{
						Marshal.FreeCoTaskMem(num);
					}
				}
				return Array.Empty<VideoFormatInfo>();
			}
			finally
			{
				DirectShowDeviceHelpers.Release(pin);
				DirectShowDeviceHelpers.Release(baseFilter);
			}
		}), cancellationToken);
	}

	private static VideoFormatInfo? TryParseFormat(int index, AMMediaType mediaType)
	{
		if (mediaType.formatPtr == IntPtr.Zero)
		{
			return null;
		}
		BitmapInfoHeader bitmapInfoHeader = null;
		long num = 0L;
		if (mediaType.formatType == FormatType.VideoInfo)
		{
			VideoInfoHeader videoInfoHeader = Marshal.PtrToStructure<VideoInfoHeader>(mediaType.formatPtr);
			if (videoInfoHeader == null)
			{
				return null;
			}
			bitmapInfoHeader = videoInfoHeader.BmiHeader;
			num = videoInfoHeader.AvgTimePerFrame;
		}
		else if (mediaType.formatType == FormatType.VideoInfo2)
		{
			VideoInfoHeader2 videoInfoHeader2 = Marshal.PtrToStructure<VideoInfoHeader2>(mediaType.formatPtr);
			if (videoInfoHeader2 == null)
			{
				return null;
			}
			bitmapInfoHeader = videoInfoHeader2.BmiHeader;
			num = videoInfoHeader2.AvgTimePerFrame;
		}
		if (bitmapInfoHeader == null)
		{
			return null;
		}
		int num2 = Math.Abs(bitmapInfoHeader.Width);
		int num3 = Math.Abs(bitmapInfoHeader.Height);
		if (num2 == 0 || num3 == 0)
		{
			return null;
		}
		double framesPerSecond = ((num > 0) ? (10000000.0 / (double)num) : 0.0);
		string pixelFormat = DirectShowDeviceHelpers.DescribePixelFormat(mediaType, bitmapInfoHeader);
		string colorSpace = DirectShowDeviceHelpers.DescribeColorSpace(mediaType);
		return new VideoFormatInfo(index, num2, num3, framesPerSecond, pixelFormat, colorSpace);
	}

	private static IEnumerable<double> GetFrameRates(IBaseFilter source, IPin pin, int index, VideoFormatInfo format, nint caps, int capsSize)
	{
		List<long> list = new List<long>();
		if (source is IAMVideoControl iAMVideoControl)
		{
			nint FrameRates = IntPtr.Zero;
			try
			{
				if (iAMVideoControl.GetFrameRateList(pin, index, new Size(format.Width, format.Height), out var ListSize, out FrameRates) >= 0 && FrameRates != IntPtr.Zero && ListSize > 0 && ListSize <= 1024)
				{
					for (int i = 0; i < ListSize; i++)
					{
						list.Add(Marshal.ReadInt64(FrameRates, i * 8));
					}
				}
			}
			finally
			{
				if (FrameRates != IntPtr.Zero)
				{
					Marshal.FreeCoTaskMem(FrameRates);
				}
			}
		}
		if (list.Count == 0 && capsSize >= Marshal.SizeOf<VideoStreamConfigCaps>())
		{
			VideoStreamConfigCaps videoStreamConfigCaps = Marshal.PtrToStructure<VideoStreamConfigCaps>(caps);
			if (videoStreamConfigCaps.MinFrameInterval > 0 && videoStreamConfigCaps.MaxFrameInterval >= videoStreamConfigCaps.MinFrameInterval)
			{
				list.Add(videoStreamConfigCaps.MinFrameInterval);
				list.Add(videoStreamConfigCaps.MaxFrameInterval);
				double[] array = new double[14]
				{
					23.976, 24.0, 25.0, 29.97, 30.0, 50.0, 59.94, 60.0, 100.0, 119.88,
					120.0, 144.0, 165.0, 240.0
				};
				foreach (double num in array)
				{
					long num2 = (long)Math.Round(10000000.0 / num);
					if (num2 >= videoStreamConfigCaps.MinFrameInterval && num2 <= videoStreamConfigCaps.MaxFrameInterval)
					{
						list.Add(num2);
					}
				}
			}
		}
		return (from interval in list
			where interval >= 10000
			select 10000000.0 / (double)interval).Distinct();
	}
}
