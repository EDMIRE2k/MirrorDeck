using System;
using MirrorDeck.Core.Models;

namespace MirrorDeck.Rendering.Layout;

public static class ViewportCalculator
{
	public static DisplaySize ResolveOutputSize(DisplaySize input, OutputSettings output)
	{
		return output.ResolutionMode switch
		{
			OutputResolutionMode.P720 => new DisplaySize(1280.0, 720.0), 
			OutputResolutionMode.P1080 => new DisplaySize(1920.0, 1080.0), 
			OutputResolutionMode.P1440 => new DisplaySize(2560.0, 1440.0), 
			OutputResolutionMode.P4K => new DisplaySize(3840.0, 2160.0), 
			OutputResolutionMode.Custom => new DisplaySize(Math.Max(1, output.CustomWidth), Math.Max(1, output.CustomHeight)), 
			_ => input.IsEmpty ? new DisplaySize(1920.0, 1080.0) : input, 
		};
	}

	public static DisplayRect CalculateViewport(DisplaySize container, DisplaySize input, OutputSettings output, ScalingSettings scaling)
	{
		if (container.IsEmpty)
		{
			return new DisplayRect(0.0, 0.0, 0.0, 0.0);
		}
		if (input.IsEmpty || !output.PreserveAspectRatio || output.AspectMode == AspectMode.Stretch)
		{
			return new DisplayRect(0.0, 0.0, container.Width, container.Height);
		}
		DisplaySize displaySize = ResolveOutputSize(input, output);
		double num = displaySize.Width / displaySize.Height;
		double num2 = container.Width / container.Height;
		AspectMode aspectMode = output.AspectMode;
		bool flag = ((aspectMode == AspectMode.Fill || aspectMode == AspectMode.Crop) ? true : false);
		double num3 = (flag ? Math.Max(container.Width / displaySize.Width, container.Height / displaySize.Height) : Math.Min(container.Width / displaySize.Width, container.Height / displaySize.Height));
		if (scaling.IntegerScaling)
		{
			num3 = Math.Max(1.0, Math.Floor(num3));
		}
		double num4 = displaySize.Width * num3;
		double num5 = displaySize.Height * num3;
		if (!scaling.IntegerScaling && aspectMode == AspectMode.Fit)
		{
			if (num2 > num)
			{
				num5 = container.Height;
				num4 = num5 * num;
			}
			else
			{
				num4 = container.Width;
				num5 = num4 / num;
			}
		}
		return new DisplayRect((container.Width - num4) / 2.0, (container.Height - num5) / 2.0, num4, num5);
	}
}
