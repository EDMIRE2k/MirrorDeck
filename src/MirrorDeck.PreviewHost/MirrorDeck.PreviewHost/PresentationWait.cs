using System;
using System.Threading;
using Microsoft.Win32.SafeHandles;
using Vortice.DXGI;

namespace MirrorDeck.PreviewHost;

internal sealed class PresentationWait : IDisposable
{
    private readonly EventWaitHandle? _ready;
    public PresentationWait(IDXGISwapChain swapChain)
    {
        using var chain = swapChain.QueryInterfaceOrNull<IDXGISwapChain2>();
        if (chain == null) return;
        chain.MaximumFrameLatency = 1;
        nint handle = chain.FrameLatencyWaitableObject;
        if (handle != 0)
        {
            _ready = new EventWaitHandle(false, EventResetMode.AutoReset);
            _ready.SafeWaitHandle = new SafeWaitHandle(handle, ownsHandle: true);
        }
    }

    public bool Wait(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (_ready == null) return true;
        int result = WaitHandle.WaitAny(new WaitHandle[] { token.WaitHandle, _ready }, 100);
        token.ThrowIfCancellationRequested();
        return result == 1;
    }
    public void Dispose() => _ready?.Dispose();
}
