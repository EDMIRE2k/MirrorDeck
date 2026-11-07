using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Globalization;
using System.Runtime.InteropServices;

namespace MirrorDeck.PreviewHost;

internal sealed class DesktopMirrorCapture : IDisposable
{
	private struct Rect
	{
		public int Left;

		public int Top;

		public int Right;

		public int Bottom;
	}

	private struct CursorInfo
	{
		public int CbSize;

		public int Flags;

		public nint Handle;

		public Point ScreenPosition;
	}

	private readonly Func<Rectangle> _getBounds;

	private volatile bool _captureCursor;
    public void SetCaptureCursor(bool enabled) => _captureCursor = enabled;

	private Bitmap? _bitmap;

	private const int CursorShowing = 1;

	private const int DrawIconNormal = 3;

	public Rectangle Bounds { get; private set; }

	private DesktopMirrorCapture(Func<Rectangle> getBounds, bool captureCursor)
	{
		_getBounds = getBounds;
		_captureCursor = captureCursor;
		Bounds = NormalizeBounds(getBounds());
	}

	public void RefreshBounds()
	{
		Bounds = NormalizeBounds(_getBounds());
	}

	public static DesktopMirrorCapture Create(string sourceId, bool captureCursor)
	{
		if (sourceId.StartsWith("mirrordeck:display:", StringComparison.OrdinalIgnoreCase))
		{
			Rectangle bounds = ParseDisplayBounds(sourceId.Substring("mirrordeck:display:".Length));
			return new DesktopMirrorCapture(() => bounds, captureCursor);
		}
		nint handle = ParseWindowHandle(sourceId);
		if (handle == IntPtr.Zero)
		{
			throw new InvalidOperationException("The selected window capture source is no longer available.");
		}
		return new DesktopMirrorCapture(() =>
		{
			if (!GetWindowRect(handle, out var lpRect))
			{
				throw new InvalidOperationException("The selected window capture source is no longer available.");
			}
			return Rectangle.FromLTRB(lpRect.Left, lpRect.Top, lpRect.Right, lpRect.Bottom);
		}, captureCursor);
	}

	public DesktopMirrorFrame? CaptureFrame(byte[] pixels)
	{
		RefreshBounds();
		if (Bounds.Width <= 0 || Bounds.Height <= 0)
		{
			return null;
		}
		int num = Bounds.Width * 4;
		int num2 = num * Bounds.Height;
		if (pixels.Length != num2)
		{
			throw new ArgumentException("Desktop frame buffer does not match the current capture bounds.", "pixels");
		}
		if (_bitmap == null || _bitmap.Width != Bounds.Width || _bitmap.Height != Bounds.Height)
		{
			_bitmap?.Dispose();
			_bitmap = new Bitmap(Bounds.Width, Bounds.Height, PixelFormat.Format32bppArgb);
		}
		using (Graphics graphics = Graphics.FromImage(_bitmap))
		{
			graphics.CopyFromScreen(Bounds.Left, Bounds.Top, 0, 0, Bounds.Size, CopyPixelOperation.SourceCopy);
			if (_captureCursor)
			{
				DrawCursor(graphics, Bounds);
			}
		}
		BitmapData bitmapData = _bitmap.LockBits(new Rectangle(0, 0, _bitmap.Width, _bitmap.Height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
		try
		{
			for (int i = 0; i < _bitmap.Height; i++)
			{
				Marshal.Copy(IntPtr.Add(bitmapData.Scan0, i * bitmapData.Stride), pixels, i * num, num);
			}
			return new DesktopMirrorFrame(pixels, _bitmap.Width, _bitmap.Height, num);
		}
		finally
		{
			_bitmap.UnlockBits(bitmapData);
		}
	}

	public void Dispose()
	{
		_bitmap?.Dispose();
	}

	private static Rectangle NormalizeBounds(Rectangle bounds)
	{
		return new Rectangle(bounds.Left, bounds.Top, Math.Max(1, bounds.Width), Math.Max(1, bounds.Height));
	}

	private static Rectangle ParseDisplayBounds(string value)
	{
		string[] array = value.Split(',');
		if (array.Length == 4 && int.TryParse(array[0], out var result) && int.TryParse(array[1], out var result2) && int.TryParse(array[2], out var result3) && int.TryParse(array[3], out var result4))
		{
			return new Rectangle(result, result2, result3, result4);
		}
		throw new InvalidOperationException("The selected display capture source is invalid.");
	}

	private static nint ParseWindowHandle(string sourceId)
	{
		string text;
		if (sourceId.StartsWith("mirrordeck:window:", StringComparison.OrdinalIgnoreCase))
		{
			text = "mirrordeck:window:";
		}
		else
		{
			text = (sourceId.StartsWith("mirrordeck:game:", StringComparison.OrdinalIgnoreCase) ? "mirrordeck:game:" : null);
		}
		if (text == null)
		{
			return IntPtr.Zero;
		}
		if (!long.TryParse(sourceId.Substring(text.Length), NumberStyles.HexNumber, null, out var result))
		{
			return IntPtr.Zero;
		}
		return new IntPtr(result);
	}

	private static void DrawCursor(Graphics graphics, Rectangle sourceBounds)
	{
		CursorInfo pci = new CursorInfo
		{
			CbSize = Marshal.SizeOf<CursorInfo>()
		};
		if (!GetCursorInfo(ref pci) || pci.Flags != 1 || pci.Handle == IntPtr.Zero)
		{
			return;
		}
		int num = pci.ScreenPosition.X - sourceBounds.Left;
		int num2 = pci.ScreenPosition.Y - sourceBounds.Top;
		if (num < -64 || num2 < -64 || num > sourceBounds.Width || num2 > sourceBounds.Height)
		{
			return;
		}
		nint hdc = graphics.GetHdc();
		try
		{
			DrawIconEx(hdc, num, num2, pci.Handle, 0, 0, 0, IntPtr.Zero, 3);
		}
		finally
		{
			graphics.ReleaseHdc(hdc);
		}
	}

	[DllImport("user32.dll")]
	private static extern bool GetWindowRect(nint hWnd, out Rect lpRect);

	[DllImport("user32.dll")]
	private static extern bool GetCursorInfo(ref CursorInfo pci);

	[DllImport("user32.dll")]
	private static extern bool DrawIconEx(nint hdc, int xLeft, int yTop, nint hIcon, int cxWidth, int cyWidth, int istepIfAniCur, nint hbrFlickerFreeDraw, int diFlags);
}
