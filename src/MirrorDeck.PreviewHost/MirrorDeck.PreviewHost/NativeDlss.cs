using System;
using System.IO;
using System.Runtime.InteropServices;
using Vortice.Direct3D12;
using MirrorDeck.Core.Models;

namespace MirrorDeck.PreviewHost;

internal sealed class NativeDlss : IDisposable
{
    private nint _context;
    private bool _active, _allocated;
    private readonly Action<string> _log;
    private string _status = "";
    internal bool Neural { get; }
    internal string Status => _context == 0 ? "Native DLSS unavailable" : Marshal.PtrToStringUTF8(MdDlssStatus(_context)) ?? "";

    internal NativeDlss(ID3D12Device device, ID3D12CommandQueue queue, Action<string> log, bool neural = false)
    {
        _log = log;
        Neural = neural;
        string runtime = Path.Combine(AppContext.BaseDirectory, "NativeDLSS");
        string cache = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MirrorDeck", "NativeDLSS-cache");
        Directory.CreateDirectory(cache);
        _context = neural ? MdNeuralCreate(device.NativePointer, queue.NativePointer, runtime, cache)
            : MdDlssCreate(device.NativePointer, queue.NativePointer, runtime, cache);
        if (_context == 0) throw new InvalidOperationException(Error());
    }

    internal unsafe ID3D12Resource Process(byte[] pixels, int stride, bool bottomUp, int width, int height, int outputWidth, int outputHeight, ScalingSettings settings)
    {
        if (_context == 0) throw new ObjectDisposedException(nameof(NativeDlss));
        if (pixels.LongLength < (long)stride * height) throw new ArgumentException("Incomplete DLSS capture frame.");
        nint result;
        _allocated = true;
        fixed (byte* data = pixels)
        {
            var options = NrOptions.From(settings.Neural ?? new());
            result = Neural ? MdNeuralProcess(_context, (nint)data, (uint)stride, bottomUp ? 1 : 0, (uint)width, (uint)height, ref options)
                : MdDlssProcessOptions(_context, (nint)data, (uint)stride, bottomUp ? 1 : 0, (uint)width, (uint)height, (uint)outputWidth, (uint)outputHeight, _active ? 0 : 1,
                (int)settings.DlssModel, (int)settings.DlssMotionQuality, (float)settings.DlssDetailProtection);
        }
        if (result == 0) throw new InvalidOperationException(Error());
        _active = true;
        string status = Status;
        if (_status != status) { _status = status; _log(status); }
        Marshal.AddRef(result);
        return new ID3D12Resource(result);
    }

    internal void Suspend()
    {
        if (_context == 0 || !_allocated) return;
        if (MdDlssSuspend(_context) == 0) throw new InvalidOperationException(Error());
        _active = false;
        _allocated = false;
    }

    public void Dispose()
    {
        if (_context == 0) return;
        MdDlssDestroy(_context);
        _context = 0;
    }

    private static string Error() => Marshal.PtrToStringUTF8(MdDlssError()) ?? "Native DLSS failed.";
    [StructLayout(LayoutKind.Sequential)]
    private struct NrOptions
    {
        public int Preset, Style, WorkHeight, AutoMask, UiCorrection;
        public float Intensity, Tone, Structure, Skin, Color, PaperWhite, Transfer;
        public int Passes;
        private static float Clamp(double value, float min, float max, float fallback) => double.IsFinite(value) ? (float)Math.Clamp(value, min, max) : fallback;
        public static NrOptions From(NeuralRenderingSettings s) => new()
        {
            Preset = Math.Clamp((int)s.Preset, 0, 3), Style = Math.Clamp((int)s.Style, 0, 2), WorkHeight = Math.Clamp(s.WorkHeight, 240, 2160),
            AutoMask = s.AutomaticMask ? 1 : 0, UiCorrection = s.UiCorrection ? 1 : 0,
            Intensity = Clamp(s.Intensity, 0, 2, 2), Tone = Clamp(s.LocalTone, 0, 2, 2), Structure = Clamp(s.LocalStructure, 0, 2, 2),
            Skin = Clamp(s.SkinStructure, -1, 1, -1), Color = Clamp(s.ColorStrength, 0, 1, 1), PaperWhite = 1, Transfer = Clamp(s.Blend, 0, 1, 1), Passes = Math.Clamp(s.Passes, 1, 4)
        };
    }
    [DllImport("MirrorDeck.NativeDlss.dll", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Unicode)]
    private static extern nint MdNeuralCreate(nint device, nint queue, string runtime, string cache);
    [DllImport("MirrorDeck.NativeDlss.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern nint MdNeuralProcess(nint context, nint pixels, uint stride, int bottomUp, uint width, uint height, ref NrOptions options);
    [DllImport("MirrorDeck.NativeDlss.dll", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Unicode)]
    private static extern nint MdDlssCreate(nint device, nint queue, string runtime, string cache);
    [DllImport("MirrorDeck.NativeDlss.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern nint MdDlssProcessOptions(nint context, nint pixels, uint stride, int bottomUp, uint width, uint height, uint outputWidth, uint outputHeight, int reset, int model, int motionQuality, float detailProtection);
    [DllImport("MirrorDeck.NativeDlss.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern nint MdDlssError();
    [DllImport("MirrorDeck.NativeDlss.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern nint MdDlssStatus(nint context);
    [DllImport("MirrorDeck.NativeDlss.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern int MdDlssSuspend(nint context);
    [DllImport("MirrorDeck.NativeDlss.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern void MdDlssDestroy(nint context);
}
