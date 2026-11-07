using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Threading;
using MirrorDeck.Core.Models;
using MirrorDeck.Rendering.Layout;
using MirrorDeck.Rendering;
using SharpGen.Runtime;
using Vortice.D3DCompiler;
using Vortice.DXGI;
using Vortice.Direct3D;
using Vortice.Direct3D12;
using Vortice.Mathematics;

namespace MirrorDeck.PreviewHost;

internal sealed class D3D12PreviewRenderer : IPreviewRenderer, IDisposable
{
    private NativeDlss? _dlss;
    private bool _dlssFailed, _nativeLastFrame;
    private string _dlssStatus = "";
    private readonly PreviewGpuInfo _gpu;
    private ScalingFilter _lastFilter;
    private readonly nint _windowHandle;
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

	private const int FrameCount = 2;

	private const int ConstantBufferSize = 256;

	private readonly Action<string> _log;

	private readonly IDXGIFactory4 _factory;

	private readonly ID3D12Device2 _device;

	private readonly ID3D12CommandQueue _commandQueue;

	private readonly ID3D12DescriptorHeap _rtvHeap;

	private readonly ID3D12DescriptorHeap _srvHeap;

	private readonly ID3D12CommandAllocator[] _commandAllocators = new ID3D12CommandAllocator[2];

	private readonly ID3D12Resource[] _renderTargets = new ID3D12Resource[2];

	private readonly ulong[] _fenceValues = new ulong[2];

	private readonly ID3D12GraphicsCommandList _commandList;

	private readonly ID3D12Fence _fence;

	private readonly AutoResetEvent _fenceEvent = new AutoResetEvent(initialState: false);

	private readonly int _rtvDescriptorSize;

	private readonly ID3D12RootSignature _rootSignature;

	private readonly ID3D12PipelineState _pipelineState;

	private readonly ID3D12Resource _constantBuffer;

	private unsafe readonly void* _constantBufferData;

	private readonly bool _tearingSupported;

	private IDXGISwapChain3 _swapChain;

	private ID3D12Resource? _sourceTexture;

	private readonly ID3D12Resource?[] _sourceUploads = new ID3D12Resource[2];

	private int _processingMaxHeight;

	private int _sourceWidth;

	private int _sourceHeight;

	private int _targetWidth;

	private int _targetHeight;

	private uint _backBufferIndex;

	private bool _disposed;

	public string BackendName => "D3D12" + _dlssStatus;

	public bool IsActive => !_disposed;

	public unsafe D3D12PreviewRenderer(nint windowHandle, System.Drawing.Size initialSize, Action<string> log)
	{
		_log = log;
        _windowHandle = windowHandle;
		_targetWidth = Math.Max(1, initialSize.Width);
		_targetHeight = Math.Max(1, initialSize.Height);
		_factory = DXGI.CreateDXGIFactory2<IDXGIFactory4>(debug: false);
		_tearingSupported = _factory.QueryInterfaceOrNull<IDXGIFactory5>()?.PresentAllowTearing == true;
		_device = PreviewGpu.CreateDevice(_factory, out _gpu);

		_commandQueue = _device.CreateCommandQueue(CommandListType.Direct);
		using IDXGISwapChain1 iDXGISwapChain = _factory.CreateSwapChainForHwnd(_commandQueue, windowHandle, CreateSwapChainDescription(), new SwapChainFullscreenDescription
		{
			Windowed = true
		});
		_swapChain = iDXGISwapChain.QueryInterface<IDXGISwapChain3>();
        _presentationWait = new PresentationWait(_swapChain);
		using (IDXGISwapChain2 iDXGISwapChain2 = _swapChain.QueryInterfaceOrNull<IDXGISwapChain2>())
		{
			if ((object)iDXGISwapChain2 != null)
			{
				iDXGISwapChain2.MaximumFrameLatency = 1u;
			}
		}
		_factory.MakeWindowAssociation(windowHandle, WindowAssociationFlags.IgnoreAltEnter);
		_rtvHeap = _device.CreateDescriptorHeap(new DescriptorHeapDescription(DescriptorHeapType.RenderTargetView, 2u));
		_srvHeap = _device.CreateDescriptorHeap(new DescriptorHeapDescription(DescriptorHeapType.ConstantBufferViewShaderResourceViewUnorderedAccessView, 2u, DescriptorHeapFlags.ShaderVisible));
		_rtvDescriptorSize = (int)_device.GetDescriptorHandleIncrementSize(DescriptorHeapType.RenderTargetView);
		CreateRenderTargets();
		for (int i = 0; i < 2; i++)
		{
			_commandAllocators[i] = _device.CreateCommandAllocator(CommandListType.Direct);
		}
		_commandList = _device.CreateCommandList<ID3D12GraphicsCommandList>(CommandListType.Direct, _commandAllocators[0]);
		_commandList.Close();
		_fence = _device.CreateFence(0uL);
		_constantBuffer = _device.CreateCommittedResource(HeapType.Upload, ResourceDescription.Buffer(512uL, ResourceFlags.None, 0uL), ResourceStates.GenericRead);
		void* constantBufferData = default;
		_constantBuffer.Map(0u, null, &constantBufferData).CheckError();
		_constantBufferData = constantBufferData;
		_fenceValues[_backBufferIndex] = 1uL;
		_rootSignature = CreateRootSignature();
		_pipelineState = CreatePipelineState();
		_log($"D3D12 GPU preview renderer initialized. target={_targetWidth}x{_targetHeight}, tearing={_tearingSupported}.");
	}

	public void Resize(System.Drawing.Size size)
	{
		var (num, num2) = OutputSettings.GetProcessingSize(size.Width, size.Height, _processingMaxHeight);
		if (num != _targetWidth || num2 != _targetHeight)
		{
			WaitForGpu();
			_targetWidth = num;
			_targetHeight = num2;
			for (int i = 0; i < 2; i++)
			{
				_renderTargets[i]?.Dispose();
				_renderTargets[i] = null;
				_fenceValues[i] = _fenceValues[_backBufferIndex];
			}
			_swapChain.ResizeBuffers(2u, (uint)_targetWidth, (uint)_targetHeight, Format.B8G8R8A8_UNorm, (SwapChainFlags)(0x40 | (_tearingSupported ? 2048 : 0))).CheckError();
			CreateRenderTargets();
		}
	}

	public unsafe void Render(byte[] frame, int sourceWidth, int sourceHeight, int sourceStride, bool sourceBottomUp, System.Drawing.Size targetSize, PreviewRenderSettings settings)
	{
		ThrowIfDisposed();
        bool nativeRequested = !settings.DisableProcessing && PreviewGpu.IsDlss(settings.Scaling.Filter) && _gpu.SupportsDlss;
        bool neural = settings.Scaling.Filter == ScalingFilter.Dlss5;
        if (_lastFilter != settings.Scaling.Filter) _dlssFailed = false;
        _lastFilter = settings.Scaling.Filter;
        // Native upscaling must reach the physical presentation surface. The
        // processing cap remains available for spatial filters and ReShade.
        _processingMaxHeight = nativeRequested && !ReShadeBootstrapper.LocalProxyLoaded && !_dlssFailed
            ? 0 : settings.Output.ProcessingMaxHeight;
		Resize(targetSize);
        var constants = CreateConstants(sourceWidth, sourceHeight, sourceBottomUp, settings);
        if (PreviewGpu.IsDlss(settings.Scaling.Filter) && !_gpu.SupportsDlss) constants.Filter = (float)ScalingFilter.FsrEasu;
        ID3D12Resource? nativeOutput = null;
        if (nativeRequested && ReShadeBootstrapper.LocalProxyLoaded)
        {
            if (!_nativeLastFrame) _log("Native DLSS bypassed: ReShade owns NGX in this session. Uninstall ReShade to use native DLSS 4.5; add-ons and presets are retained.");
            _dlssStatus = " / ReShade active; native DLSS bypassed";
        }
        else if (nativeRequested && !_dlssFailed)
        {
            try
            {
                var monitor = settings.Scaling.DlssOutputResolution == DlssOutputResolution.CurrentMonitor
                    ? System.Windows.Forms.Screen.FromHandle(_windowHandle).Bounds.Size : targetSize;
                var (width, height) = DlssOutputSizing.Resolve(settings.Scaling.DlssOutputResolution, sourceWidth, sourceHeight,
                    (int)Math.Round(constants.ViewportWidth), (int)Math.Round(constants.ViewportHeight), monitor.Width, monitor.Height);
                if (neural) { width = sourceWidth; height = sourceHeight; }
                if (width >= sourceWidth && height >= sourceHeight)
                {
                    if (_dlss != null && _dlss.Neural != neural) { WaitForGpu(); _dlss.Dispose(); _dlss = null; }
                    _dlss ??= new NativeDlss(_device, _commandQueue, _log, neural);
                    nativeOutput = _dlss.Process(frame, sourceStride, sourceBottomUp, sourceWidth, sourceHeight, width, height, settings.Scaling);
                    sourceWidth = width; sourceHeight = height; sourceBottomUp = false;
                    constants.SourceWidth = width; constants.SourceHeight = height; constants.BottomUp = 0;
                    constants.Sharpness = double.IsFinite(settings.Scaling.DlssSharpness) ? (float)Math.Clamp(settings.Scaling.DlssSharpness, 0, 1) : 0;
                    if (neural)
                    {
                        constants.Filter = (float)ScalingFilter.FsrEasu;
                        constants.UpscaleStrength = (float)Math.Clamp(settings.Scaling.UpscaleStrength, 0, 1);
                        constants.Sharpness = (float)Math.Clamp(settings.Scaling.Sharpness, 0, 1);
                    }
                    _dlssStatus = " / native " + _dlss.Status + $"; surface={_targetWidth}x{_targetHeight}";
                }
                else { _dlss?.Suspend(); _dlssStatus = " / DLSS bypass (downscaling)"; }
            }
            catch (Exception ex)
            {
                _dlssFailed = true;
                _dlssStatus = " / DLSS unavailable: bilinear fallback";
                _log("Native DLSS unavailable; continuing with bilinear. " + ex.Message);
                _dlss?.Suspend();
            }
        }
        else if (!nativeRequested)
        {
            _dlss?.Suspend();
            _dlssStatus = "";
            // An explicit off/on selection retries a failed native initialization.
            if (_nativeLastFrame) _dlssFailed = false;
        }
        _nativeLastFrame = nativeRequested;
        using var nativeFrame = nativeOutput;
        if (nativeOutput == null) EnsureSourceTexture(sourceWidth, sourceHeight);
		ID3D12CommandAllocator iD3D12CommandAllocator = _commandAllocators[_backBufferIndex];
		iD3D12CommandAllocator.Reset();
		_commandList.Reset(iD3D12CommandAllocator, _pipelineState);
		if (nativeOutput == null) fixed (byte* pData = frame)
		{
			SubresourceData subresourceData = new SubresourceData
			{
				pData = pData,
				RowPitch = sourceStride,
				SlicePitch = sourceStride * sourceHeight
			};
			_commandList.UpdateSubresources(_sourceTexture, _sourceUploads[_backBufferIndex], 0uL, 0u, 1u, &subresourceData);
		}
        if (nativeOutput == null) _commandList.ResourceBarrierTransition(_sourceTexture, ResourceStates.CopyDest, ResourceStates.PixelShaderResource);
        BindSource(nativeOutput ?? _sourceTexture!, nativeOutput == null ? Format.B8G8R8A8_UNorm : Format.R16G16B16A16_Float);
		_commandList.ResourceBarrierTransition(_renderTargets[_backBufferIndex], ResourceStates.Common, ResourceStates.RenderTarget);
		Marshal.StructureToPtr(constants, (nint)((byte*)_constantBufferData + _backBufferIndex * 256), fDeleteOld: false);
		CpuDescriptorHandle rtvHandle = GetRtvHandle(_backBufferIndex);
		_commandList.ClearRenderTargetView(rtvHandle, new Color4(0f, 0f, 0f));
		_commandList.OMSetRenderTargets(rtvHandle);
		_commandList.RSSetViewport(new Viewport(0f, 0f, _targetWidth, _targetHeight));
		_commandList.RSSetScissorRect(_targetWidth, _targetHeight);
		ReadOnlySpan<ID3D12DescriptorHeap> descriptorHeaps = new ReadOnlySpan<ID3D12DescriptorHeap>(_srvHeap);
		_commandList.SetDescriptorHeaps(1, descriptorHeaps);
		_commandList.SetGraphicsRootSignature(_rootSignature);
		_commandList.SetGraphicsRootConstantBufferView(0u, _constantBuffer.GPUVirtualAddress + _backBufferIndex * 256);
		_commandList.SetGraphicsRootDescriptorTable(1u, _srvHeap.GetGPUDescriptorHandleForHeapStart() + (int)_backBufferIndex * (int)_device.GetDescriptorHandleIncrementSize(DescriptorHeapType.ConstantBufferViewShaderResourceViewUnorderedAccessView));
		_commandList.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
		_commandList.DrawInstanced(3u, 1u, 0u, 0u);
		_commandList.ResourceBarrierTransition(_renderTargets[_backBufferIndex], ResourceStates.RenderTarget, ResourceStates.Common);
        if (nativeOutput == null) _commandList.ResourceBarrierTransition(_sourceTexture, ResourceStates.PixelShaderResource, ResourceStates.CopyDest);
		_commandList.Close();
		_commandQueue.ExecuteCommandList(_commandList);
		PresentFlags flags = ((!settings.VSyncEnabled && _tearingSupported) ? PresentFlags.AllowTearing : PresentFlags.None);
		_swapChain.Present(settings.VSyncEnabled ? 1u : 0u, flags).CheckError();
		MoveToNextFrame();
	}

	public void Dispose()
	{
        _presentationWait?.Dispose();
        _presentationWait = null;
		if (!_disposed)
		{
			_disposed = true;
			try
			{
				WaitForGpu();
			}
			catch (Exception ex)
			{
				_log("GPU shutdown wait failed: " + ex.Message);
			}
            _dlss?.Dispose();
            _dlss = null;
			_constantBuffer.Unmap(0u);
			ID3D12Resource[] sourceUploads = _sourceUploads;
			for (int i = 0; i < sourceUploads.Length; i++)
			{
				sourceUploads[i]?.Dispose();
			}
			_sourceTexture?.Dispose();
			_constantBuffer.Dispose();
			_pipelineState.Dispose();
			_rootSignature.Dispose();
			_fence.Dispose();
			_commandList.Dispose();
			ID3D12CommandAllocator[] commandAllocators = _commandAllocators;
			for (int i = 0; i < commandAllocators.Length; i++)
			{
				commandAllocators[i]?.Dispose();
			}
			sourceUploads = _renderTargets;
			for (int i = 0; i < sourceUploads.Length; i++)
			{
				sourceUploads[i]?.Dispose();
			}
			_srvHeap.Dispose();
			_rtvHeap.Dispose();
			_swapChain.Dispose();
			_commandQueue.Dispose();
			_device.Dispose();
			_factory.Dispose();
			_fenceEvent.Dispose();
		}
	}

	private SwapChainDescription1 CreateSwapChainDescription()
	{
		return new SwapChainDescription1
		{
			Width = (uint)_targetWidth,
			Height = (uint)_targetHeight,
			Format = Format.B8G8R8A8_UNorm,
			BufferCount = 2u,
			BufferUsage = Usage.RenderTargetOutput,
			SampleDescription = SampleDescription.Default,
			Scaling = Scaling.Stretch,
			SwapEffect = SwapEffect.FlipDiscard,
			AlphaMode = AlphaMode.Ignore,
			Flags = (SwapChainFlags)(0x40 | (_tearingSupported ? 2048 : 0))
		};
	}

	private ID3D12Device2 CreateHardwareDevice()
	{
		using IDXGIFactory6 iDXGIFactory = _factory.QueryInterfaceOrNull<IDXGIFactory6>();
		if ((object)iDXGIFactory != null)
		{
			IDXGIAdapter1 adapter;
			for (uint num = 0u; iDXGIFactory.EnumAdapterByGpuPreference<IDXGIAdapter1>(num, GpuPreference.HighPerformance, out adapter).Success; num++)
			{
				using (adapter)
				{
					if ((object)adapter == null || (adapter.Description1.Flags & AdapterFlags.Software) != 0 || !D3D12.D3D12CreateDevice<ID3D12Device2>(adapter, FeatureLevel.Level_11_0, out ID3D12Device2 device).Success || (object)device == null)
					{
						continue;
					}
					return device;
				}
			}
		}
		IDXGIAdapter1 adapterOut;
		for (uint num2 = 0u; _factory.EnumAdapters1(num2, out adapterOut).Success; num2++)
		{
			using (adapterOut)
			{
				if ((object)adapterOut == null || (adapterOut.Description1.Flags & AdapterFlags.Software) != 0 || !D3D12.D3D12CreateDevice<ID3D12Device2>(adapterOut, FeatureLevel.Level_11_0, out ID3D12Device2 device2).Success || (object)device2 == null)
				{
					continue;
				}
				return device2;
			}
		}
		throw new InvalidOperationException("No hardware Direct3D 12 device is available.");
	}

	private ID3D12RootSignature CreateRootSignature()
	{
		RootSignatureFlags flags = RootSignatureFlags.AllowInputAssemblerInputLayout | RootSignatureFlags.DenyHullShaderRootAccess | RootSignatureFlags.DenyDomainShaderRootAccess | RootSignatureFlags.DenyGeometryShaderRootAccess;
		RootDescriptor1 rootDescriptor = new RootDescriptor1(0u, 0u, RootDescriptorFlags.DataStaticWhileSetAtExecute);
		DescriptorRange1 descriptorRange = new DescriptorRange1(DescriptorRangeType.ShaderResourceView, 1u, 0u, 0u, uint.MaxValue, DescriptorRangeFlags.DataStaticWhileSetAtExecute);
		return _device.CreateRootSignature(new RootSignatureDescription1(flags, new RootParameter1[2]
		{
			new RootParameter1(RootParameterType.ConstantBufferView, rootDescriptor, ShaderVisibility.Pixel),
			new RootParameter1(new RootDescriptorTable1(descriptorRange), ShaderVisibility.Pixel)
		}, new StaticSamplerDescription[2]
		{
			new StaticSamplerDescription(SamplerDescription.PointClamp, ShaderVisibility.Pixel, 0u, 0u),
			new StaticSamplerDescription(SamplerDescription.LinearClamp, ShaderVisibility.Pixel, 1u, 0u)
		}));
	}

	private ID3D12PipelineState CreatePipelineState()
	{
		byte[] array = CompileShaderBytes(PreviewShaders.Source, "VSMain", "vs_5_0");
		byte[] array2 = CompileShaderBytes(PreviewShaders.Source, "PSMain", "ps_5_0");
		return _device.CreateGraphicsPipelineState<ID3D12PipelineState>(new GraphicsPipelineStateDescription
		{
			RootSignature = _rootSignature,
			VertexShader = array,
			PixelShader = array2,
			PrimitiveTopologyType = PrimitiveTopologyType.Triangle,
			RasterizerState = RasterizerDescription.CullNone,
			BlendState = BlendDescription.Opaque,
			DepthStencilState = DepthStencilDescription.None,
			RenderTargetFormats = new Format[1] { Format.B8G8R8A8_UNorm },
			SampleDescription = SampleDescription.Default
		});
	}

	private void CreateRenderTargets()
	{
		_backBufferIndex = _swapChain.CurrentBackBufferIndex;
		for (uint num = 0u; num < 2; num++)
		{
			_renderTargets[num] = _swapChain.GetBuffer<ID3D12Resource>(num);
			_device.CreateRenderTargetView(_renderTargets[num], null, GetRtvHandle(num));
		}
	}

	private CpuDescriptorHandle GetRtvHandle(uint index)
	{
		return _rtvHeap.GetCPUDescriptorHandleForHeapStart() + (int)index * _rtvDescriptorSize;
	}

	private void EnsureSourceTexture(int width, int height)
	{
		if ((object)_sourceTexture == null || width != _sourceWidth || height != _sourceHeight)
		{
			WaitForGpu();
			ID3D12Resource[] sourceUploads = _sourceUploads;
			for (int i = 0; i < sourceUploads.Length; i++)
			{
				sourceUploads[i]?.Dispose();
			}
			_sourceTexture?.Dispose();
			_sourceWidth = width;
			_sourceHeight = height;
			_sourceTexture = _device.CreateCommittedResource(HeapType.Default, ResourceDescription.Texture2D(Format.B8G8R8A8_UNorm, (uint)Math.Max(1, width), (uint)Math.Max(1, height), 1, 0, 1u, 0u, ResourceFlags.None, TextureLayout.Unknown, 0uL), ResourceStates.CopyDest);
			ulong requiredIntermediateSize = _device.GetRequiredIntermediateSize(_sourceTexture, 0u, 1u);
			for (int j = 0; j < 2; j++)
			{
				_sourceUploads[j] = _device.CreateCommittedResource(HeapType.Upload, ResourceDescription.Buffer(requiredIntermediateSize, ResourceFlags.None, 0uL), ResourceStates.GenericRead);
			}
			_device.CreateShaderResourceView(_sourceTexture, new ShaderResourceViewDescription
			{
				Shader4ComponentMapping = ShaderComponentMapping.Default,
				Format = Format.B8G8R8A8_UNorm,
				ViewDimension = Vortice.Direct3D12.ShaderResourceViewDimension.Texture2D,
				Texture2D = new Texture2DShaderResourceView
				{
					MipLevels = 1u
				}
			}, _srvHeap.GetCPUDescriptorHandleForHeapStart());
			_log($"D3D12 source texture ready: {width}x{height}, uploadBytes={requiredIntermediateSize}.");
		}
	}

    private void BindSource(ID3D12Resource texture, Format format)
    {
        _device.CreateShaderResourceView(texture, new ShaderResourceViewDescription
        {
            Shader4ComponentMapping = ShaderComponentMapping.Default,
            Format = format,
            ViewDimension = Vortice.Direct3D12.ShaderResourceViewDimension.Texture2D,
            Texture2D = new Texture2DShaderResourceView { MipLevels = 1 }
        }, _srvHeap.GetCPUDescriptorHandleForHeapStart() + (int)_backBufferIndex * (int)_device.GetDescriptorHandleIncrementSize(DescriptorHeapType.ConstantBufferViewShaderResourceViewUnorderedAccessView));
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

	private void WaitForGpu()
	{
		ulong value = _fenceValues[_backBufferIndex];
		_commandQueue.Signal(_fence, value).CheckError();
		_fence.SetEventOnCompletion(value, _fenceEvent).CheckError();
		if (!_fenceEvent.WaitOne(TimeSpan.FromSeconds(5.0)))
		{
			throw new TimeoutException("The preview GPU did not finish within five seconds.");
		}
		_fenceValues[_backBufferIndex]++;
	}

	private void MoveToNextFrame()
	{
		ulong num = _fenceValues[_backBufferIndex];
		_commandQueue.Signal(_fence, num).CheckError();
		_backBufferIndex = _swapChain.CurrentBackBufferIndex;
		if (_fence.CompletedValue < _fenceValues[_backBufferIndex])
		{
			_fence.SetEventOnCompletion(_fenceValues[_backBufferIndex], _fenceEvent).CheckError();
			if (!_fenceEvent.WaitOne(TimeSpan.FromSeconds(5.0)))
			{
				throw new TimeoutException("The preview GPU did not finish within five seconds.");
			}
		}
		_fenceValues[_backBufferIndex] = num + 1;
	}

	private void ThrowIfDisposed()
	{
		if (_disposed)
		{
			throw new ObjectDisposedException("D3D12PreviewRenderer");
		}
	}

	private static byte[] CompileShaderBytes(string source, string entryPoint, string target)
	{
		Result result = Compiler.Compile(source, Array.Empty<ShaderMacro>(), null, entryPoint, "MirrorDeckPreview.hlsl", target, ShaderFlags.OptimizationLevel3, EffectFlags.None, out Blob blob, out Blob errorBlob);
		if (result.Failure)
		{
			string text = (((object)errorBlob == null) ? result.Code.ToString() : (Marshal.PtrToStringAnsi(errorBlob.BufferPointer) ?? result.Code.ToString()));
			errorBlob?.Dispose();
			throw new InvalidOperationException("D3D shader compile failed for " + entryPoint + ": " + text);
		}
		byte[] array = new byte[(uint)blob.BufferSize];
		Marshal.Copy(blob.BufferPointer, array, 0, array.Length);
		errorBlob?.Dispose();
		blob.Dispose();
		return array;
	}
}
