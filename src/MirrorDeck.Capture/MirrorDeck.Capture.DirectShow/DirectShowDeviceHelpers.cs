using System;
using System.Linq;
using System.Runtime.InteropServices;
using DirectShowLib;
using MirrorDeck.Core.Services;

namespace MirrorDeck.Capture.DirectShow;

internal static class DirectShowDeviceHelpers
{
	public static DsDevice? FindVideoDevice(string id)
	{
		return DsDevice.GetDevicesOfCat(FilterCategory.VideoInputDevice).FirstOrDefault((DsDevice device) => string.Equals(device.DevicePath, id, StringComparison.OrdinalIgnoreCase) || string.Equals(device.Name, id, StringComparison.OrdinalIgnoreCase));
	}

	public static IBaseFilter CreateFilter(DsDevice device)
	{
		Guid riidResult = typeof(IBaseFilter).GUID;
		device.Mon.BindToObject(null, null, ref riidResult, out object ppvResult);
		return (IBaseFilter)ppvResult;
	}

	public static IPin? FindCaptureOrPreviewPin(IBaseFilter source)
	{
		return DsFindPin.ByCategory(source, PinCategory.Capture, 0) ?? DsFindPin.ByCategory(source, PinCategory.Preview, 0) ?? DsFindPin.ByDirection(source, PinDirection.Output, 0);
	}

	public static void TryApplyFormat(IBaseFilter source, int formatIndex, IAppLogger logger, double fps = 0.0)
	{
		IPin pin = null;
		AMMediaType ppmt = null;
		nint num = IntPtr.Zero;
		try
		{
			pin = FindCaptureOrPreviewPin(source);
			if (pin is IAMStreamConfig iAMStreamConfig)
			{
				DsError.ThrowExceptionForHR(iAMStreamConfig.GetNumberOfCapabilities(out var piCount, out var piSize));
				if (formatIndex >= 0 && formatIndex < piCount)
				{
					num = Marshal.AllocCoTaskMem(piSize);
					DsError.ThrowExceptionForHR(iAMStreamConfig.GetStreamCaps(formatIndex, out ppmt, num));
					DirectShowFrameRate.Apply(ppmt, fps);
					DsError.ThrowExceptionForHR(iAMStreamConfig.SetFormat(ppmt));
				}
			}
		}
		catch (Exception exception)
		{
			logger.Error($"Applying DirectShow format index {formatIndex} failed; falling back to device auto mode.", exception);
		}
		finally
		{
			if (ppmt != null)
			{
				DsUtils.FreeAMMediaType(ppmt);
			}
			if (num != IntPtr.Zero)
			{
				Marshal.FreeCoTaskMem(num);
			}
			Release(pin);
		}
	}

	public static string DescribePixelFormat(AMMediaType mediaType, BitmapInfoHeader bitmap)
	{
		Guid subType = mediaType.subType;
		if (subType == MediaSubType.NV12)
		{
			return "NV12";
		}
		if (subType == MediaSubType.YUY2)
		{
			return "YUY2";
		}
		if (subType == MediaSubType.UYVY)
		{
			return "UYVY";
		}
		if (subType == MediaSubType.MJPG)
		{
			return "MJPEG";
		}
		if (subType == MediaSubType.RGB24)
		{
			return "RGB24";
		}
		if (subType == MediaSubType.RGB32)
		{
			return "RGB32";
		}
		if (bitmap.Compression != 0)
		{
			return FourCc(bitmap.Compression);
		}
		return $"RGB{bitmap.BitCount}";
	}

	public static string DescribeColorSpace(AMMediaType mediaType)
	{
		if (mediaType.subType == MediaSubType.NV12 || mediaType.subType == MediaSubType.YUY2 || mediaType.subType == MediaSubType.UYVY)
		{
			return "YUV";
		}
		if (mediaType.subType == MediaSubType.RGB24 || mediaType.subType == MediaSubType.RGB32)
		{
			return "RGB";
		}
		return "Unknown";
	}

	public static void Release(object? comObject)
	{
		if (comObject != null && Marshal.IsComObject(comObject))
		{
			Marshal.ReleaseComObject(comObject);
		}
	}

	private static string FourCc(int value)
	{
		_003C_003Ey__InlineArray4<char> buffer = default;
		buffer[0] = (char)(value & 0xFF);
		buffer[1] = (char)((value >> 8) & 0xFF);
		buffer[2] = (char)((value >> 16) & 0xFF);
		buffer[3] = (char)((value >> 24) & 0xFF);
		return new string((Span<char>)buffer).Trim();
	}
}
