using System;
using System.Buffers;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Drawing;
using System.Globalization;
using System.Runtime.InteropServices;
using Vortice.DXGI;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using WinRT;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;

namespace MirrorDeck.PreviewHost;

internal sealed class WindowsGraphicsCaptureMirror : IDisposable
{
	private delegate bool MonitorEnumProc(nint hMonitor, nint hdcMonitor, nint lprcMonitor, nint dwData);

	private struct NativeRect
	{
		public int Left;

		public int Top;

		public int Right;

		public int Bottom;
	}

	[ComImport]
	[Guid("3628E81B-3CAC-4C60-B7F4-23CE0E0C3356")]
	[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
	private interface IGraphicsCaptureItemInterop
	{
		[PreserveSig]
		int CreateForWindow(nint window, ref Guid iid, out nint result);

		[PreserveSig]
		int CreateForMonitor(nint monitor, ref Guid iid, out nint result);
	}

	[ComImport]
	[Guid("A9B3D012-3DF2-4EE3-B8D1-8695F457D3C1")]
	[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
	private interface IDirect3DDxgiInterfaceAccess
	{
		[PreserveSig]
		int GetInterface(ref Guid iid, out nint p);
	}

	private sealed class GraphicsCaptureItemFactory : IDisposable
	{
		private nint _factoryPtr;

		public IGraphicsCaptureItemInterop Interop { get; }

		private GraphicsCaptureItemFactory(nint factoryPtr)
		{
			_factoryPtr = factoryPtr;
			Interop = (IGraphicsCaptureItemInterop)Marshal.GetObjectForIUnknown(factoryPtr);
		}

		public static GraphicsCaptureItemFactory Create()
		{
			Marshal.ThrowExceptionForHR(WindowsCreateString("Windows.Graphics.Capture.GraphicsCaptureItem", "Windows.Graphics.Capture.GraphicsCaptureItem".Length, out var hstring));
			try
			{
				Guid iid = GraphicsCaptureItemInteropGuid;
				Marshal.ThrowExceptionForHR(RoGetActivationFactory(hstring, ref iid, out var factory));
				return new GraphicsCaptureItemFactory(factory);
			}
			finally
			{
				WindowsDeleteString(hstring);
			}
		}

		public void Dispose()
		{
			if (_factoryPtr != IntPtr.Zero)
			{
				Marshal.Release(_factoryPtr);
				_factoryPtr = IntPtr.Zero;
			}
		}
	}

	private readonly Action<byte[], int, int, int, double> _onFrame;

	private readonly Action<string> _log;

	private readonly ID3D11Device _d3dDevice;

	private readonly ID3D11DeviceContext _d3dContext;

	private readonly IDirect3DDevice _captureDevice;

	private readonly GraphicsCaptureItem _item;

	private readonly object _frameLock = new object();

	private Direct3D11CaptureFramePool? _framePool;

	private GraphicsCaptureSession? _session;

    private sealed class ReadbackSlot
    {
        public ID3D11Texture2D? Texture;
        public double Timestamp;
        public bool Pending;
    }
    private readonly ReadbackSlot[] _readbacks = { new(), new(), new() };
    private readonly Queue<ReadbackSlot> _pending = new();
    private readonly AutoResetEvent _available = new(false);
    private Thread? _worker;
    private long _busyDrops;

	private int _width;

	private int _height;

	private volatile bool _disposed;

	private static readonly Guid GraphicsCaptureItemGuid = Guid.Parse("79C3F95B-31F7-4EC2-A464-632EF5D30760");

	private static readonly Guid GraphicsCaptureItemInteropGuid = Guid.Parse("3628E81B-3CAC-4C60-B7F4-23CE0E0C3356");

	public int Width => _width;

	public int Height => _height;

	private WindowsGraphicsCaptureMirror(GraphicsCaptureItem item, bool captureCursor, Action<byte[], int, int, int, double> onFrame, Action<string> log)
	{
		_item = item;
		_onFrame = onFrame;
		_log = log;
		_width = Math.Max(1, item.Size.Width);
		_height = Math.Max(1, item.Size.Height);
		CreateD3DDevice(out _d3dDevice, out _d3dContext);
		_captureDevice = CreateDirect3DDevice(_d3dDevice);
		_framePool = Direct3D11CaptureFramePool.CreateFreeThreaded(_captureDevice, DirectXPixelFormat.B8G8R8A8UIntNormalized, 3, _item.Size);
		_session = _framePool.CreateCaptureSession(_item);
		_session.IsCursorCaptureEnabled = captureCursor;
		_framePool.FrameArrived += OnFrameArrived;
		_item.Closed += (GraphicsCaptureItem _, object _) =>
		{
			Dispose();
		};
	}

	public static WindowsGraphicsCaptureMirror Create(string sourceId, bool captureCursor, Action<byte[], int, int, int, double> onFrame, Action<string> log)
	{
		if (!GraphicsCaptureSession.IsSupported())
		{
			throw new NotSupportedException("Windows Graphics Capture is not supported on this Windows installation.");
		}
		return new WindowsGraphicsCaptureMirror(sourceId.StartsWith("mirrordeck:display:", StringComparison.OrdinalIgnoreCase) ? CreateItemForMonitor(sourceId) : CreateItemForWindow(sourceId), captureCursor, onFrame, log);
	}

	public void Start()
	{
		ThrowIfDisposed();
        _worker = new Thread(CaptureLoop) { IsBackground = true, Name = "MirrorDeck WGC readback" };
        _worker.Start();
        _session?.StartCapture();
		_log($"Windows Graphics Capture session started: {_width}x{_height}.");
	}

    public void SetCaptureCursor(bool enabled)
    {
        if (!_disposed && _session != null && _session.IsCursorCaptureEnabled != enabled)
            _session.IsCursorCaptureEnabled = enabled;
    }

    public void Dispose()
    {
        lock (_frameLock)
        {
            if (_disposed) return;
            _disposed = true;
            if (_framePool != null) _framePool.FrameArrived -= OnFrameArrived;
        }
        _available.Set();
        if (_worker == null) DisposeCaptureResources();
        else if (Thread.CurrentThread != _worker && !_worker.Join(2000))
            _log("WGC readback is still stopping; its worker retains ownership until it exits.");
    }

    private void DisposeCaptureResources()
    {
        _session?.Dispose();
        _framePool?.Dispose();
        foreach (var slot in _readbacks) slot.Texture?.Dispose();
        _captureDevice.Dispose();
        _d3dContext.Dispose();
        _d3dDevice.Dispose();
        _log($"WGC readback stopped. Backpressure drops={_busyDrops}.");
    }

    private void OnFrameArrived(Direct3D11CaptureFramePool sender, object args)
    {
        if (!_disposed) _available.Set();
    }

    private void CaptureLoop()
    {
        using var deadlines = new FrameDeadlineWait();
        try
        {
            while (!_disposed)
            {
                deadlines.Wait(Stopwatch.Frequency / (_pending.Count == 0 ? 10 : 1000), CancellationToken.None, _available);
                if (_disposed) break;
                DrainReadbacks();
                bool submitted = false;
                // A bounded pool limits memory and prevents the producer from blocking on the GPU.
                for (int i = 0; i < 3 && !_disposed; i++)
                {
                    var frame = _framePool?.TryGetNextFrame();
                    if (frame == null) break;
                    try
                    {
                        int width = frame.ContentSize.Width, height = frame.ContentSize.Height;
                        if (width <= 0 || height <= 0) continue;
                        if (width != _width || height != _height)
                        {
                            var size = frame.ContentSize;
                            frame.Dispose();
                            frame = null;
                            foreach (var slot in _readbacks)
                            {
                                slot.Texture?.Dispose(); slot.Texture = null; slot.Pending = false;
                            }
                            _pending.Clear();
                            _width = width; _height = height;
                            _framePool?.Recreate(_captureDevice, DirectXPixelFormat.B8G8R8A8UIntNormalized, 3, size);
                            break;
                        }
                        ReadbackSlot? free = null;
                        foreach (var slot in _readbacks) if (!slot.Pending) { free = slot; break; }
                        if (free == null) { _busyDrops++; continue; }
                        using var source = GetD3D11Texture(frame.Surface);
                        // Frame-pool surfaces may temporarily retain old dimensions during a resize.
                        if (source.Description.Width != width || source.Description.Height != height) continue;
                        free.Texture ??= _d3dDevice.CreateTexture2D(new Texture2DDescription
                        {
                            Width = (uint)width, Height = (uint)height, MipLevels = 1, ArraySize = 1,
                            Format = Format.B8G8R8A8_UNorm, SampleDescription = new SampleDescription(1, 0),
                            Usage = ResourceUsage.Staging, CPUAccessFlags = CpuAccessFlags.Read,
                            BindFlags = BindFlags.None, MiscFlags = ResourceOptionFlags.None
                        });
                        free.Timestamp = frame.SystemRelativeTime.TotalSeconds;
                        _d3dContext.CopyResource(free.Texture, source);
                        submitted = true;
                        free.Pending = true;
                        _pending.Enqueue(free);
                    }
                    finally { frame?.Dispose(); }
                }
                if (submitted) _d3dContext.Flush();
                DrainReadbacks();
            }
        }
        catch (Exception ex)
        {
            _log("Windows Graphics Capture readback failed: " + ex);
        }
        finally
        {
            _disposed = true;
            DisposeCaptureResources();
        }
    }

    private void DrainReadbacks()
    {
        while (_pending.Count > 0 && !_disposed)
        {
            var slot = _pending.Peek();
            var result = _d3dContext.Map(slot.Texture!, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.DoNotWait, out var mapped);
            if (result.Code == unchecked((int)0x887A000A)) return; // DXGI_ERROR_WAS_STILL_DRAWING
            result.CheckError();
            int stride = checked(_width * 4);
            byte[] pixels = ArrayPool<byte>.Shared.Rent(checked(stride * _height));
            try
            {
                for (int row = 0; row < _height; row++)
                    Marshal.Copy(mapped.DataPointer + row * (int)mapped.RowPitch, pixels, row * stride, stride);
            }
            catch { ArrayPool<byte>.Shared.Return(pixels); throw; }
            finally { _d3dContext.Unmap(slot.Texture!, 0); }
            _pending.Dequeue();
            slot.Pending = false;
            _onFrame(pixels, _width, _height, stride, slot.Timestamp);
        }
    }

	private static GraphicsCaptureItem CreateItemForMonitor(string sourceId)
	{
		nint num = FindMonitorForBounds(ParseDisplayBounds(sourceId.Substring("mirrordeck:display:".Length)));
		if (num == IntPtr.Zero)
		{
			throw new InvalidOperationException("The selected display capture source is no longer available.");
		}
		using GraphicsCaptureItemFactory graphicsCaptureItemFactory = GraphicsCaptureItemFactory.Create();
		IGraphicsCaptureItemInterop interop = graphicsCaptureItemFactory.Interop;
		Guid iid = GraphicsCaptureItemGuid;
		Marshal.ThrowExceptionForHR(interop.CreateForMonitor(num, ref iid, out var result));
		return MarshalInterface<GraphicsCaptureItem>.FromAbi(result);
	}

	private static GraphicsCaptureItem CreateItemForWindow(string sourceId)
	{
		nint num = ParseWindowHandle(sourceId);
		if (num == IntPtr.Zero || !IsWindow(num))
		{
			throw new InvalidOperationException("The selected window capture source is no longer available.");
		}
		using GraphicsCaptureItemFactory graphicsCaptureItemFactory = GraphicsCaptureItemFactory.Create();
		IGraphicsCaptureItemInterop interop = graphicsCaptureItemFactory.Interop;
		Guid iid = GraphicsCaptureItemGuid;
		Marshal.ThrowExceptionForHR(interop.CreateForWindow(num, ref iid, out var result));
		return MarshalInterface<GraphicsCaptureItem>.FromAbi(result);
	}

	private static Rectangle ParseDisplayBounds(string value)
	{
		string[] array = value.Split(',');
		if (array.Length == 4 && int.TryParse(array[0], out var result) && int.TryParse(array[1], out var result2) && int.TryParse(array[2], out var result3) && int.TryParse(array[3], out var result4))
		{
			return new Rectangle(result, result2, Math.Max(1, result3), Math.Max(1, result4));
		}
		throw new InvalidOperationException("The selected display capture source is invalid.");
	}

	private static nint FindMonitorForBounds(Rectangle targetBounds)
	{
		nint result = IntPtr.Zero;
		EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (nint monitor, nint _, nint rect, nint _) =>
		{
			NativeRect nativeRect = Marshal.PtrToStructure<NativeRect>(rect);
			if (Rectangle.FromLTRB(nativeRect.Left, nativeRect.Top, nativeRect.Right, nativeRect.Bottom) == targetBounds)
			{
				result = monitor;
				return false;
			}
			return true;
		}, IntPtr.Zero);
		return result;
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

	private static void CreateD3DDevice(out ID3D11Device device, out ID3D11DeviceContext context)
	{
		FeatureLevel[] featureLevels = new FeatureLevel[2]
		{
			FeatureLevel.Level_11_1,
			FeatureLevel.Level_11_0
		};
		D3D11.D3D11CreateDevice(null, DriverType.Hardware, DeviceCreationFlags.BgraSupport, featureLevels, out device, out var _, out context).CheckError();
	}

	private static IDirect3DDevice CreateDirect3DDevice(ID3D11Device device)
	{
		using IDXGIDevice iDXGIDevice = device.QueryInterface<IDXGIDevice>();
		Marshal.ThrowExceptionForHR(CreateDirect3D11DeviceFromDXGIDevice(iDXGIDevice.NativePointer, out var graphicsDevice));
		return MarshalInterface<IDirect3DDevice>.FromAbi(graphicsDevice);
	}

	private static ID3D11Texture2D GetD3D11Texture(IDirect3DSurface surface)
	{
		IDirect3DDxgiInterfaceAccess direct3DDxgiInterfaceAccess = surface.As<IDirect3DDxgiInterfaceAccess>();
		Guid iid = typeof(ID3D11Texture2D).GUID;
		Marshal.ThrowExceptionForHR(direct3DDxgiInterfaceAccess.GetInterface(ref iid, out var p));
		return new ID3D11Texture2D(p);
	}

	private void ThrowIfDisposed()
	{
		if (_disposed)
		{
			throw new ObjectDisposedException("WindowsGraphicsCaptureMirror");
		}
	}

	[DllImport("d3d11.dll")]
	private static extern int CreateDirect3D11DeviceFromDXGIDevice(nint dxgiDevice, out nint graphicsDevice);

	[DllImport("combase.dll")]
	private static extern int RoGetActivationFactory(nint activatableClassId, ref Guid iid, out nint factory);

	[DllImport("combase.dll", CharSet = CharSet.Unicode)]
	private static extern int WindowsCreateString(string sourceString, int length, out nint hstring);

	[DllImport("combase.dll")]
	private static extern int WindowsDeleteString(nint hstring);

	[DllImport("user32.dll")]
	private static extern bool EnumDisplayMonitors(nint hdc, nint lprcClip, MonitorEnumProc lpfnEnum, nint dwData);

	[DllImport("user32.dll")]
	private static extern bool IsWindow(nint hWnd);
}
