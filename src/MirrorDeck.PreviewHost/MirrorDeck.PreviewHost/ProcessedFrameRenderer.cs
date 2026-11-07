using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using MirrorDeck.Core.Models;
using MirrorDeck.Rendering.Layout;

namespace MirrorDeck.PreviewHost;

internal static class ProcessedFrameRenderer
{
	private readonly record struct SourceFrame(byte[] Bytes, int Width, int Height, int Stride, bool BottomUp)
	{
		public void Read(int x, int y, out double r, out double g, out double b)
		{
			x = Math.Clamp(x, 0, Width - 1);
			y = Math.Clamp(y, 0, Height - 1);
			int num = (BottomUp ? (Height - 1 - y) : y) * Stride + x * 4;
			if (num < 0 || num + 2 >= Bytes.Length)
			{
				r = 0.0;
				g = 0.0;
				b = 0.0;
			}
			else
			{
				b = (int)Bytes[num];
				g = (int)Bytes[num + 1];
				r = (int)Bytes[num + 2];
			}
		}
	}

	private const double TwoPi = Math.PI * 2.0;

	public static Bitmap Render(byte[] source, int sourceWidth, int sourceHeight, int sourceStride, bool sourceBottomUp, Size outputSize, PreviewRenderSettings settings)
	{
		int outputWidth = Math.Max(1, outputSize.Width);
		int num = Math.Max(1, outputSize.Height);
		byte[] pixels = new byte[outputWidth * num * 4];
		FillOpaqueBlack(pixels);
		OutputSettings outputSettings = CloneOutput(settings.Output);
		ScalingSettings scalingSettings = CloneScaling(settings.Scaling);
		EnhancementSettings enhancement = settings.Enhancement;
		ColorSettings color = settings.Color;
		if (settings.DisableProcessing)
		{
			scalingSettings.Filter = ScalingFilter.Bilinear;
			scalingSettings.Sharpness = 0.0;
			scalingSettings.Debanding = false;
			enhancement = new EnhancementSettings();
			color = new ColorSettings();
		}
		if (!scalingSettings.BlackBars && outputSettings.AspectMode == AspectMode.Fit)
		{
			outputSettings.AspectMode = AspectMode.Fill;
		}
		DisplayRect displayRect = ViewportCalculator.CalculateViewport(new DisplaySize(outputWidth, num), new DisplaySize(sourceWidth, sourceHeight), outputSettings, scalingSettings);
		int x0 = Math.Clamp((int)Math.Floor(displayRect.X), 0, outputWidth);
		int num2 = Math.Clamp((int)Math.Floor(displayRect.Y), 0, num);
		int x1 = Math.Clamp((int)Math.Ceiling(displayRect.X + displayRect.Width), 0, outputWidth);
		int num3 = Math.Clamp((int)Math.Ceiling(displayRect.Y + displayRect.Height), 0, num);
		if (x1 <= x0 || num3 <= num2 || sourceWidth <= 0 || sourceHeight <= 0)
		{
			return CreateBitmap(outputWidth, num, pixels);
		}
		SourceFrame sourceInfo = new SourceFrame(source, sourceWidth, sourceHeight, sourceStride, sourceBottomUp);
		ScalingFilter filter = scalingSettings.Filter;
		double viewportX = displayRect.X;
		double viewportY = displayRect.Y;
		double viewportWidth = Math.Max(1.0, displayRect.Width);
		double viewportHeight = Math.Max(1.0, displayRect.Height);
		Parallel.For(num2, num3, (int y) =>
		{
			double y2 = ((double)y - viewportY + 0.5) / viewportHeight * (double)sourceHeight - 0.5;
			for (int i = x0; i < x1; i++)
			{
				double x2 = ((double)i - viewportX + 0.5) / viewportWidth * (double)sourceWidth - 0.5;
				Sample(sourceInfo, x2, y2, filter, out var r, out var g, out var b);
				ApplyColor(color, ref r, ref g, ref b);
				int num4 = (y * outputWidth + i) * 4;
				pixels[num4] = ToByte(b);
				pixels[num4 + 1] = ToByte(g);
				pixels[num4 + 2] = ToByte(r);
				pixels[num4 + 3] = byte.MaxValue;
			}
		});
		ApplyPostProcessing(pixels, outputWidth, num, new Rectangle(x0, num2, x1 - x0, num3 - num2), scalingSettings, enhancement);
		return CreateBitmap(outputWidth, num, pixels);
	}

	private static void ApplyPostProcessing(byte[] pixels, int width, int height, Rectangle rect, ScalingSettings scaling, EnhancementSettings enhancement)
	{
		double num = Math.Clamp(scaling.Sharpness, 0.0, 1.0);
		double num2 = (enhancement.CleanUpscalingEnabled ? Math.Clamp(enhancement.CleanUpscalingStrength, 0.0, 1.0) : 0.0);
		double denoise = (enhancement.CleanUpscalingEnabled ? (Math.Clamp(enhancement.Denoise, 0.0, 1.0) * num2) : 0.0);
		double num3 = (enhancement.CleanUpscalingEnabled ? (Math.Clamp(enhancement.EdgeRestore, 0.0, 1.0) * num2) : 0.0);
		if (num > 0.001 || denoise > 0.001 || num3 > 0.001)
		{
			byte[] original = new byte[pixels.Length];
			Buffer.BlockCopy(pixels, 0, original, 0, pixels.Length);
			int fromInclusive = Math.Max(rect.Top + 1, 1);
			int toExclusive = Math.Min(rect.Bottom - 1, height - 1);
			int startX = Math.Max(rect.Left + 1, 1);
			int endX = Math.Min(rect.Right - 1, width - 1);
			double sharpenAmount = num * 0.85 + num3 * 1.25;
			Parallel.For(fromInclusive, toExclusive, (int y) =>
			{
				for (int i = startX; i < endX; i++)
				{
					int num4 = (y * width + i) * 4;
					for (int j = 0; j < 3; j++)
					{
						byte b = original[num4 + j];
						double num5 = (double)(original[num4 - 4 + j] + original[num4 + 4 + j] + original[num4 - width * 4 + j] + original[num4 + width * 4 + j]) * 0.25;
						double num6 = (double)(int)b - num5;
						double num7 = Math.Min(1.0, Math.Abs(num6) / 42.0);
						double value = Lerp((int)b, num5, denoise * (1.0 - num7)) + num6 * sharpenAmount * (0.35 + num7 * 0.65);
						pixels[num4 + j] = ToByte(value);
					}
				}
			});
		}
		if (scaling.Debanding)
		{
			ApplyOrderedDither(pixels, width, rect);
		}
	}

	private static void ApplyOrderedDither(byte[] pixels, int width, Rectangle rect)
	{
		int[,] array = new int[4, 4]
		{
			{ 0, 8, 2, 10 },
			{ 12, 4, 14, 6 },
			{ 3, 11, 1, 9 },
			{ 15, 7, 13, 5 }
		};
		for (int i = rect.Top; i < rect.Bottom; i++)
		{
			for (int j = rect.Left; j < rect.Right; j++)
			{
				int num = (i * width + j) * 4;
				double num2 = ((double)array[i & 3, j & 3] - 7.5) * 0.55;
				pixels[num] = ToByte((double)(int)pixels[num] + num2);
				pixels[num + 1] = ToByte((double)(int)pixels[num + 1] + num2);
				pixels[num + 2] = ToByte((double)(int)pixels[num + 2] + num2);
			}
		}
	}

	private static void Sample(SourceFrame frame, double x, double y, ScalingFilter filter, out double r, out double g, out double b)
	{
		switch (filter)
		{
		case ScalingFilter.NearestNeighbor:
			frame.Read((int)Math.Round(x), (int)Math.Round(y), out r, out g, out b);
			break;
		case ScalingFilter.Bicubic:
			SampleBicubic(frame, x, y, out r, out g, out b);
			break;
		case ScalingFilter.Lanczos:
			SampleLanczos(frame, x, y, out r, out g, out b);
			break;
		default:
			SampleBilinear(frame, x, y, out r, out g, out b);
			break;
		}
	}

	private static void SampleBilinear(SourceFrame frame, double x, double y, out double r, out double g, out double b)
	{
		int num = (int)Math.Floor(x);
		int num2 = (int)Math.Floor(y);
		double t = x - (double)num;
		double t2 = y - (double)num2;
		frame.Read(num, num2, out var r2, out var g2, out var b2);
		frame.Read(num + 1, num2, out var r3, out var g3, out var b3);
		frame.Read(num, num2 + 1, out var r4, out var g4, out var b4);
		frame.Read(num + 1, num2 + 1, out var r5, out var g5, out var b5);
		r = Lerp(Lerp(r2, r3, t), Lerp(r4, r5, t), t2);
		g = Lerp(Lerp(g2, g3, t), Lerp(g4, g5, t), t2);
		b = Lerp(Lerp(b2, b3, t), Lerp(b4, b5, t), t2);
	}

	private static void SampleBicubic(SourceFrame frame, double x, double y, out double r, out double g, out double b)
	{
		int num = (int)Math.Floor(x);
		int num2 = (int)Math.Floor(y);
		r = 0.0;
		g = 0.0;
		b = 0.0;
		double num3 = 0.0;
		for (int i = -1; i <= 2; i++)
		{
			double num4 = CubicWeight(y - (double)(num2 + i));
			for (int j = -1; j <= 2; j++)
			{
				double num5 = CubicWeight(x - (double)(num + j)) * num4;
				frame.Read(num + j, num2 + i, out var r2, out var g2, out var b2);
				r += r2 * num5;
				g += g2 * num5;
				b += b2 * num5;
				num3 += num5;
			}
		}
		if (Math.Abs(num3) > 0.0001)
		{
			r /= num3;
			g /= num3;
			b /= num3;
		}
	}

	private static void SampleLanczos(SourceFrame frame, double x, double y, out double r, out double g, out double b)
	{
		int num = (int)Math.Floor(x);
		int num2 = (int)Math.Floor(y);
		r = 0.0;
		g = 0.0;
		b = 0.0;
		double num3 = 0.0;
		for (int i = num2 - 2 + 1; i <= num2 + 2; i++)
		{
			double num4 = LanczosWeight(y - (double)i, 2);
			if (Math.Abs(num4) < 1E-05)
			{
				continue;
			}
			for (int j = num - 2 + 1; j <= num + 2; j++)
			{
				double num5 = LanczosWeight(x - (double)j, 2) * num4;
				if (!(Math.Abs(num5) < 1E-05))
				{
					frame.Read(j, i, out var r2, out var g2, out var b2);
					r += r2 * num5;
					g += g2 * num5;
					b += b2 * num5;
					num3 += num5;
				}
			}
		}
		if (Math.Abs(num3) > 0.0001)
		{
			r /= num3;
			g /= num3;
			b /= num3;
		}
	}

	private static void ApplyColor(ColorSettings color, ref double r, ref double g, ref double b)
	{
		if (color.RgbRange == RgbRangeMode.Limited)
		{
			r = (r - 16.0) * 255.0 / 219.0;
			g = (g - 16.0) * 255.0 / 219.0;
			b = (b - 16.0) * 255.0 / 219.0;
		}
		double num = Math.Clamp(color.Contrast, 0.0, 3.0);
		r = (r - 128.0) * num + 128.0 + color.Brightness * 255.0;
		g = (g - 128.0) * num + 128.0 + color.Brightness * 255.0;
		b = (b - 128.0) * num + 128.0 + color.Brightness * 255.0;
		double num2 = r * 0.299 + g * 0.587 + b * 0.114;
		double num3 = Math.Clamp(color.Saturation, 0.0, 3.0);
		r = num2 + (r - num2) * num3;
		g = num2 + (g - num2) * num3;
		b = num2 + (b - num2) * num3;
		if (Math.Abs(color.Hue) > 0.001)
		{
			RotateHue(ref r, ref g, ref b, color.Hue * Math.PI / 180.0);
		}
		double num4 = Math.Clamp(color.Gamma, 0.05, 6.0);
		if (Math.Abs(num4 - 1.0) > 0.001)
		{
			double y = 1.0 / num4;
			r = Math.Pow(Math.Clamp(r / 255.0, 0.0, 1.0), y) * 255.0;
			g = Math.Pow(Math.Clamp(g / 255.0, 0.0, 1.0), y) * 255.0;
			b = Math.Pow(Math.Clamp(b / 255.0, 0.0, 1.0), y) * 255.0;
		}
	}

	private static void RotateHue(ref double r, ref double g, ref double b, double radians)
	{
		double num = 0.299 * r + 0.587 * g + 0.114 * b;
		double num2 = 0.596 * r - 0.274 * g - 0.322 * b;
		double num3 = 0.211 * r - 0.523 * g + 0.312 * b;
		double num4 = Math.Cos(radians);
		double num5 = Math.Sin(radians);
		double num6 = num2 * num4 - num3 * num5;
		double num7 = num2 * num5 + num3 * num4;
		r = num + 0.956 * num6 + 0.621 * num7;
		g = num - 0.272 * num6 - 0.647 * num7;
		b = num - 1.106 * num6 + 1.703 * num7;
	}

	private static Bitmap CreateBitmap(int width, int height, byte[] pixels)
	{
		Bitmap bitmap = new Bitmap(width, height, PixelFormat.Format32bppArgb);
		BitmapData bitmapData = bitmap.LockBits(new Rectangle(0, 0, width, height), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
		try
		{
			Marshal.Copy(pixels, 0, bitmapData.Scan0, Math.Min(pixels.Length, Math.Abs(bitmapData.Stride) * height));
		}
		finally
		{
			bitmap.UnlockBits(bitmapData);
		}
		return bitmap;
	}

	private static void FillOpaqueBlack(byte[] pixels)
	{
		for (int i = 3; i < pixels.Length; i += 4)
		{
			pixels[i] = byte.MaxValue;
		}
	}

	private static OutputSettings CloneOutput(OutputSettings source)
	{
		return new OutputSettings
		{
			ResolutionMode = source.ResolutionMode,
			CustomWidth = source.CustomWidth,
			CustomHeight = source.CustomHeight,
			PreserveAspectRatio = source.PreserveAspectRatio,
			AspectMode = source.AspectMode
		};
	}

	private static ScalingSettings CloneScaling(ScalingSettings source)
	{
		return new ScalingSettings
		{
			Filter = source.Filter,
			IntegerScaling = source.IntegerScaling,
			BlackBars = source.BlackBars,
			Sharpness = source.Sharpness,
			Debanding = source.Debanding,
			UpscaleStrength = source.UpscaleStrength
		};
	}

	private static double CubicWeight(double x)
	{
		x = Math.Abs(x);
		if (!(x <= 1.0))
		{
			if (!(x < 2.0))
			{
				return 0.0;
			}
			return ((-0.5 * x + 2.5) * x - 4.0) * x + 2.0;
		}
		return (1.5 * x - 2.5) * x * x + 1.0;
	}

	private static double LanczosWeight(double x, int radius)
	{
		x = Math.Abs(x);
		if (x < 1E-06)
		{
			return 1.0;
		}
		if (x >= (double)radius)
		{
			return 0.0;
		}
		double num = Math.PI * x;
		return (double)radius * Math.Sin(num) * Math.Sin(num / (double)radius) / (num * num);
	}

	private static double Lerp(double a, double b, double t)
	{
		return a + (b - a) * t;
	}

	private static byte ToByte(double value)
	{
		return (byte)Math.Clamp((int)Math.Round(value), 0, 255);
	}
}
