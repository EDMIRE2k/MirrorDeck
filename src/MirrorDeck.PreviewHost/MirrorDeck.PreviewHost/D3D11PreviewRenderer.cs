using System;
using System.Threading;
using System.Drawing;
using System.Runtime.InteropServices;
using MirrorDeck.Core.Models;
using MirrorDeck.Rendering.Layout;
using SharpGen.Runtime;
using Vortice.D3DCompiler;
using Vortice.DXGI;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.Mathematics;

namespace MirrorDeck.PreviewHost;

internal sealed class D3D11PreviewRenderer : IPreviewRenderer, IDisposable
{
    private PresentationWait? _presentationWait;
    public bool WaitForPresentation(CancellationToken token) => _presentationWait?.Wait(token) ?? true;
    public bool TryGetDisplayStatistics(out DisplayedFrameStatistics statistics)
    {
        bool valid = _swapChain.GetFrameStatistics(out var value).Success && value.PresentCount > 0;
        statistics = new(value.PresentCount, value.PresentRefreshCount);
        return valid;
    }
	private struct ShaderConstants
	{
		public float SourceWidth;

		public float SourceHeight;

		public float TargetWidth;

		public float TargetHeight;

		public float ViewportX;

		public float ViewportY;

		public float ViewportWidth;

		public float ViewportHeight;

		public float Brightness;

		public float Contrast;

		public float Saturation;

		public float Gamma;

		public float HueSin;

		public float HueCos;

		public float Sharpness;

		public float CleanStrength;

		public float Denoise;

		public float EdgeRestore;

		public float LimitedRange;

		public float BottomUp;

		public float Filter;

		public float Debanding;

		public float DisableProcessing;

		public float UpscaleStrength;
	}

	private readonly ID3D11Device _device;

	private readonly ID3D11DeviceContext _context;

	private readonly IDXGISwapChain _swapChain;

	private readonly ID3D11VertexShader _vertexShader;

	private readonly ID3D11PixelShader _pixelShader;

	private readonly ID3D11SamplerState _pointSampler;

	private readonly ID3D11SamplerState _linearSampler;

	private readonly ID3D11Buffer _constantsBuffer;

	private readonly Action<string> _log;

	private ID3D11RenderTargetView? _renderTargetView;

	private ID3D11Texture2D? _sourceTexture;

	private ID3D11ShaderResourceView? _sourceView;

	private int _sourceWidth;

	private int _sourceHeight;

	private int _targetWidth;

	private int _targetHeight;

	private bool _disposed;

	private int _processingMaxHeight;

	internal static string ShaderSource => PreviewShaders.Source;

    private bool _nativeRequested;
	public string BackendName => _nativeRequested ? "D3D11 / native DLSS requires DX12: bilinear fallback" : "D3D11";

	public bool IsActive => !_disposed;

	public unsafe D3D11PreviewRenderer(nint windowHandle, System.Drawing.Size initialSize, Action<string> log)
	{
		_log = log;
		_targetWidth = Math.Max(1, initialSize.Width);
		_targetHeight = Math.Max(1, initialSize.Height);
		SwapChainDescription value = new SwapChainDescription
		{
			BufferDescription = new ModeDescription((uint)_targetWidth, (uint)_targetHeight, new Rational(60u, 1u), Format.B8G8R8A8_UNorm),
			SampleDescription = new SampleDescription(1u, 0u),
			BufferUsage = Usage.RenderTargetOutput,
			BufferCount = 2u,
			OutputWindow = windowHandle,
			Windowed = true,
			SwapEffect = SwapEffect.FlipDiscard,
			Flags = SwapChainFlags.FrameLatencyWaitableObject
		};
		FeatureLevel[] featureLevels = new FeatureLevel[4]
		{
			FeatureLevel.Level_11_1,
			FeatureLevel.Level_11_0,
			FeatureLevel.Level_10_1,
			FeatureLevel.Level_10_0
		};
		FeatureLevel? featureLevel = null;
		D3D11.D3D11CreateDeviceAndSwapChain(null, DriverType.Hardware, DeviceCreationFlags.BgraSupport, featureLevels, value, out IDXGISwapChain swapChain, out ID3D11Device device, out featureLevel, out ID3D11DeviceContext immediateContext).CheckError();
		_swapChain = swapChain ?? throw new InvalidOperationException("D3D11 did not return a swap chain.");
        _presentationWait = new PresentationWait(_swapChain);
		_device = device ?? throw new InvalidOperationException("D3D11 did not return a device.");
		_context = immediateContext ?? throw new InvalidOperationException("D3D11 did not return a device context.");
		using Blob blob = CompileShader(PreviewShaders.Source, "VSMain", "vs_5_0");
		using Blob blob2 = CompileShader(PreviewShaders.Source, "PSMain", "ps_5_0");
		_vertexShader = _device.CreateVertexShader(((IntPtr)blob.BufferPointer).ToPointer(), blob.BufferSize);
		_pixelShader = _device.CreatePixelShader(((IntPtr)blob2.BufferPointer).ToPointer(), blob2.BufferSize);
		_pointSampler = _device.CreateSamplerState(new SamplerDescription(Filter.MinMagMipPoint, TextureAddressMode.Clamp, 0f, 1u, ComparisonFunction.Never, 0f));
		_linearSampler = _device.CreateSamplerState(new SamplerDescription(Filter.MinMagMipLinear, TextureAddressMode.Clamp, 0f, 1u, ComparisonFunction.Never, 0f));
		_constantsBuffer = _device.CreateBuffer((uint)Marshal.SizeOf<ShaderConstants>(), BindFlags.ConstantBuffer);
		CreateRenderTarget();
		_log($"D3D11 GPU preview renderer initialized. featureLevel={featureLevel}, target={_targetWidth}x{_targetHeight}.");
	}

	public void Resize(System.Drawing.Size size)
	{
		var (num, num2) = OutputSettings.GetProcessingSize(size.Width, size.Height, _processingMaxHeight);
		if (num != _targetWidth || num2 != _targetHeight)
		{
			_targetWidth = num;
			_targetHeight = num2;
			_renderTargetView?.Dispose();
			_renderTargetView = null;
            _context.OMSetRenderTargets((ID3D11RenderTargetView?)null);
			_swapChain.ResizeBuffers(0u, (uint)_targetWidth, (uint)_targetHeight, Format.Unknown, SwapChainFlags.FrameLatencyWaitableObject).CheckError();
			CreateRenderTarget();
		}
	}

	public void Render(byte[] frame, int sourceWidth, int sourceHeight, int sourceStride, bool sourceBottomUp, System.Drawing.Size targetSize, PreviewRenderSettings settings)
	{
		ThrowIfDisposed();
        _nativeRequested = !settings.DisableProcessing && settings.Scaling.Filter == ScalingFilter.Dlss45;
		_processingMaxHeight = settings.Output.ProcessingMaxHeight;
		Resize(targetSize);
		EnsureSourceTexture(sourceWidth, sourceHeight);
		_context.UpdateSubresource(frame, _sourceTexture, 0u, (uint)sourceStride, (uint)(sourceStride * sourceHeight));
		ShaderConstants value = CreateConstants(sourceWidth, sourceHeight, sourceBottomUp, settings);
		_context.UpdateSubresource(in value, _constantsBuffer);
		ID3D11RenderTargetView renderTargetView = _renderTargetView ?? throw new InvalidOperationException("D3D11 render target is not ready.");
		ID3D11ShaderResourceView shaderResourceView = _sourceView ?? throw new InvalidOperationException("D3D11 source view is not ready.");
		_context.ClearRenderTargetView(renderTargetView, new Color4(0f, 0f, 0f));
		_context.OMSetRenderTargets(renderTargetView);
		_context.RSSetViewport(0f, 0f, _targetWidth, _targetHeight);
		_context.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
		_context.VSSetShader(_vertexShader);
		_context.PSSetShader(_pixelShader);
		_context.PSSetShaderResource(0u, shaderResourceView);
		_context.PSSetSampler(0u, _pointSampler);
		_context.PSSetSampler(1u, _linearSampler);
		_context.PSSetConstantBuffer(0u, _constantsBuffer);
		_context.Draw(3u, 0u);
		_context.PSSetShaderResource(0u, null);
		_swapChain.Present(settings.VSyncEnabled ? 1u : 0u, PresentFlags.None).CheckError();
	}

	public void Dispose()
	{
        _presentationWait?.Dispose();
        _presentationWait = null;
		if (!_disposed)
		{
			_disposed = true;
			_renderTargetView?.Dispose();
			_sourceView?.Dispose();
			_sourceTexture?.Dispose();
			_constantsBuffer.Dispose();
			_linearSampler.Dispose();
			_pointSampler.Dispose();
			_pixelShader.Dispose();
			_vertexShader.Dispose();
			_swapChain.Dispose();
			_context.Dispose();
			_device.Dispose();
		}
	}

	private void EnsureSourceTexture(int width, int height)
	{
		if ((object)_sourceTexture == null || width != _sourceWidth || height != _sourceHeight)
		{
			_sourceView?.Dispose();
			_sourceTexture?.Dispose();
			_sourceWidth = width;
			_sourceHeight = height;
			_sourceTexture = _device.CreateTexture2D(new Texture2DDescription
			{
				Width = (uint)Math.Max(1, width),
				Height = (uint)Math.Max(1, height),
				MipLevels = 1u,
				ArraySize = 1u,
				Format = Format.B8G8R8A8_UNorm,
				SampleDescription = new SampleDescription(1u, 0u),
				Usage = ResourceUsage.Default,
				BindFlags = BindFlags.ShaderResource,
				CPUAccessFlags = CpuAccessFlags.None,
				MiscFlags = ResourceOptionFlags.None
			});
			_sourceView = _device.CreateShaderResourceView(_sourceTexture);
			_log($"D3D11 source texture ready: {width}x{height}.");
		}
	}

	private void CreateRenderTarget()
	{
		using ID3D11Texture2D resource = _swapChain.GetBuffer<ID3D11Texture2D>(0u);
		_renderTargetView = _device.CreateRenderTargetView(resource);
	}

	private ShaderConstants CreateConstants(int sourceWidth, int sourceHeight, bool sourceBottomUp, PreviewRenderSettings settings)
	{
		OutputSettings outputSettings = CloneOutput(settings.Output);
		ScalingSettings scalingSettings = CloneScaling(settings.Scaling);
		if (!scalingSettings.BlackBars && outputSettings.AspectMode == AspectMode.Fit)
		{
			outputSettings.AspectMode = AspectMode.Fill;
		}
		DisplayRect displayRect = ViewportCalculator.CalculateViewport(new DisplaySize(_targetWidth, _targetHeight), new DisplaySize(sourceWidth, sourceHeight), outputSettings, scalingSettings);
		ColorSettings color = settings.Color;
		double num = color.Hue * Math.PI / 180.0;
		return new ShaderConstants
		{
			SourceWidth = Math.Max(1, sourceWidth),
			SourceHeight = Math.Max(1, sourceHeight),
			TargetWidth = Math.Max(1, _targetWidth),
			TargetHeight = Math.Max(1, _targetHeight),
			ViewportX = (float)displayRect.X,
			ViewportY = (float)displayRect.Y,
			ViewportWidth = (float)Math.Max(1.0, displayRect.Width),
			ViewportHeight = (float)Math.Max(1.0, displayRect.Height),
			Brightness = (float)color.Brightness,
			Contrast = (float)Math.Clamp(color.Contrast, 0.0, 3.0),
			Saturation = (float)Math.Clamp(color.Saturation, 0.0, 3.0),
			Gamma = (float)Math.Clamp(color.Gamma, 0.05, 6.0),
			HueSin = (float)Math.Sin(num),
			HueCos = (float)Math.Cos(num),
			Sharpness = (float)Math.Clamp(scalingSettings.Sharpness, 0.0, 1.0),
			CleanStrength = (settings.Enhancement.CleanUpscalingEnabled ? ((float)Math.Clamp(settings.Enhancement.CleanUpscalingStrength, 0.0, 1.0)) : 0f),
			Denoise = (float)Math.Clamp(settings.Enhancement.Denoise, 0.0, 1.0),
			EdgeRestore = (float)Math.Clamp(settings.Enhancement.EdgeRestore, 0.0, 1.0),
			LimitedRange = ((color.RgbRange == RgbRangeMode.Limited) ? 1f : 0f),
			BottomUp = (sourceBottomUp ? 1f : 0f),
			Filter = (settings.DisableProcessing ? 1f : ((float)scalingSettings.Filter)),
			Debanding = ((!settings.DisableProcessing && scalingSettings.Debanding) ? 1f : 0f),
			DisableProcessing = (settings.DisableProcessing ? 1f : 0f),
			UpscaleStrength = double.IsFinite(scalingSettings.UpscaleStrength) ? (float)Math.Clamp(scalingSettings.UpscaleStrength, 0, 1) : 1f
		};
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

	private void ThrowIfDisposed()
	{
		if (_disposed)
		{
			throw new ObjectDisposedException("D3D11PreviewRenderer");
		}
	}

	private static Blob CompileShader(string source, string entryPoint, string target)
	{
		Result result = Compiler.Compile(source, Array.Empty<ShaderMacro>(), null, entryPoint, "MirrorDeckPreview.hlsl", target, ShaderFlags.OptimizationLevel3, EffectFlags.None, out Blob blob, out Blob errorBlob);
		if (result.Failure)
		{
			string text = (((object)errorBlob == null) ? result.Code.ToString() : (Marshal.PtrToStringAnsi(errorBlob.BufferPointer) ?? result.Code.ToString()));
			errorBlob?.Dispose();
			throw new InvalidOperationException("D3D shader compile failed for " + entryPoint + ": " + text);
		}
		errorBlob?.Dispose();
		return blob;
	}
}
