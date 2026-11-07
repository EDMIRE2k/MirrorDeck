using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace MirrorDeck.App.Interop;

internal static class Win11WindowStyler
{
	private struct Margins
	{
		public int Left;

		public int Right;

		public int Top;

		public int Bottom;
	}

	private const int DwmwaUseImmersiveDarkMode = 20;

	private const int DwmwaWindowCornerPreference = 33;

	private const int DwmwaSystemBackdropType = 38;

	public static void Apply(Window window, bool useDarkMode)
	{
		nint handle = new WindowInteropHelper(window).Handle;
		if (handle != IntPtr.Zero)
		{
			TrySet(handle, 20, useDarkMode ? 1 : 0);
			TrySet(handle, 33, 2);
			bool flag = OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22621) && !SystemParameters.HighContrast;
			int pvAttribute = ((!flag) ? 1 : 3);
			bool flag2 = DwmSetWindowAttribute(handle, 38, ref pvAttribute, 4) >= 0;
			Margins margins = new Margins
			{
				Left = ((flag & flag2) ? (-1) : 0)
			};
			DwmExtendFrameIntoClientArea(handle, ref margins);
			window.Background = ((flag & flag2) ? Brushes.Transparent : ((Brush)Application.Current.Resources["AppBackgroundBrush"]));
			HwndTarget hwndTarget = HwndSource.FromHwnd(handle)?.CompositionTarget;
			if (hwndTarget != null)
			{
				hwndTarget.BackgroundColor = ((flag & flag2) ? Colors.Transparent : ((SolidColorBrush)Application.Current.Resources["AppBackgroundBrush"]).Color);
			}
		}
	}

	private static void TrySet(nint handle, int attribute, int value)
	{
		DwmSetWindowAttribute(handle, attribute, ref value, 4);
	}

	[DllImport("dwmapi.dll")]
	private static extern int DwmSetWindowAttribute(nint hwnd, int dwAttribute, ref int pvAttribute, int cbAttribute);

	[DllImport("dwmapi.dll")]
	private static extern int DwmExtendFrameIntoClientArea(nint hwnd, ref Margins margins);
}
