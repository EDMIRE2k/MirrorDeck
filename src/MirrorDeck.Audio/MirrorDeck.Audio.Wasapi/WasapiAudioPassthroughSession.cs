using System;
using System.Threading;
using System.Threading.Tasks;
using MirrorDeck.Core.Models;
using MirrorDeck.Core.Services;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace MirrorDeck.Audio.Wasapi;

public sealed class WasapiAudioPassthroughSession : IAudioPassthroughSession, IAsyncDisposable
{
	private readonly IAppLogger _logger;

	private readonly object _sync = new object();

	private WasapiCapture? _capture;

	private WasapiOut? _output;

	private BufferedWaveProvider? _buffer;

	private VolumeSampleProvider? _volumeProvider;

	private int _dropBytesRemaining;

	private AudioSettings _settings = new AudioSettings();

	private long _receivedBytes;

	private int _receivedBuffers;

	private DateTimeOffset _lastTelemetryUtc = DateTimeOffset.MinValue;

	public bool IsRunning { get; private set; }

	public WasapiAudioPassthroughSession(IAppLogger logger)
	{
		_logger = logger;
	}

	public Task StartAsync(AudioDeviceInfo inputDevice, AudioDeviceInfo? outputDevice, AudioSettings settings, CancellationToken cancellationToken = default(CancellationToken))
	{
		return Task.Run(() =>
		{
			cancellationToken.ThrowIfCancellationRequested();
			lock (_sync)
			{
				StopInternal();
				_settings = CopySettings(settings);
				using MMDeviceEnumerator mMDeviceEnumerator = new MMDeviceEnumerator();
				MMDevice device = mMDeviceEnumerator.GetDevice(inputDevice.Id);
				MMDevice mMDevice = (((object)outputDevice == null) ? mMDeviceEnumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia) : mMDeviceEnumerator.GetDevice(outputDevice.Id));
				_capture = new WasapiCapture(device);
				WaveFormat mixFormat = mMDevice.AudioClient.MixFormat;
				_buffer = new BufferedWaveProvider(_capture.WaveFormat)
				{
					BufferDuration = TimeSpan.FromMilliseconds(Math.Max(250, Math.Abs(settings.DelayMilliseconds) + 180)),
					DiscardOnBufferOverflow = true,
					ReadFully = true
				};
				int num = AlignBytes(_capture.WaveFormat.AverageBytesPerSecond / 100, _capture.WaveFormat.BlockAlign);
				if (num > 0)
				{
					_buffer.AddSamples(new byte[num], 0, num);
				}
				if (settings.DelayMilliseconds > 0)
				{
					int num2 = AlignBytes(_capture.WaveFormat.AverageBytesPerSecond * settings.DelayMilliseconds / 1000, _capture.WaveFormat.BlockAlign);
					if (num2 > 0)
					{
						_buffer.AddSamples(new byte[num2], 0, num2);
					}
				}
				else if (settings.DelayMilliseconds < 0)
				{
					_dropBytesRemaining = AlignBytes(_capture.WaveFormat.AverageBytesPerSecond * Math.Abs(settings.DelayMilliseconds) / 1000, _capture.WaveFormat.BlockAlign);
				}
				ISampleProvider sampleProvider = _buffer.ToSampleProvider();
				if (settings.ChannelMode == AudioChannelMode.Mono && sampleProvider.WaveFormat.Channels == 2)
				{
					sampleProvider = new StereoToMonoSampleProvider(sampleProvider);
				}
				sampleProvider = MatchOutputFormat(sampleProvider, mixFormat);
				_volumeProvider = new VolumeSampleProvider(sampleProvider)
				{
					Volume = ((settings.Muted || !settings.Enabled) ? 0f : ((float)Math.Clamp(settings.Volume, 0.0, 2.0)))
				};
				_output = new WasapiOut(mMDevice, AudioClientShareMode.Shared, useEventSync: true, 30);
				_output.Init(_volumeProvider);
				_capture.DataAvailable += OnDataAvailable;
				_capture.RecordingStopped += OnRecordingStopped;
				_output.Play();
				_capture.StartRecording();
				IsRunning = true;
				_lastTelemetryUtc = DateTimeOffset.UtcNow;
				_receivedBytes = 0L;
				_receivedBuffers = 0;
				_logger.Info($"Started audio passthrough: {inputDevice.Name} [{DescribeWaveFormat(_capture.WaveFormat)}] -> {mMDevice.FriendlyName} [mix {DescribeWaveFormat(mixFormat)}], volume={settings.Volume:0.00}, muted={settings.Muted}, playback={_output.PlaybackState}");
			}
		}, cancellationToken);
	}

	public Task StopAsync(CancellationToken cancellationToken = default(CancellationToken))
	{
		return Task.Run(() =>
		{
			cancellationToken.ThrowIfCancellationRequested();
			if (!Monitor.TryEnter(_sync, TimeSpan.FromSeconds(1.0)))
			{
				_logger.Error("Skipping audio stop because the audio graph is busy.");
				return;
			}
			try
			{
				StopInternal();
			}
			finally
			{
				Monitor.Exit(_sync);
			}
		}, cancellationToken);
	}

	public void UpdateSettings(AudioSettings settings)
	{
		if (!Monitor.TryEnter(_sync))
		{
			_logger.Info("Skipped live audio settings update because audio graph is busy.");
			return;
		}
		try
		{
			_settings = CopySettings(settings);
			if (_volumeProvider != null)
			{
				_volumeProvider.Volume = ((settings.Muted || !settings.Enabled) ? 0f : ((float)Math.Clamp(settings.Volume, 0.0, 2.0)));
			}
		}
		finally
		{
			Monitor.Exit(_sync);
		}
	}

	public async ValueTask DisposeAsync()
	{
		await StopAsync().ConfigureAwait(continueOnCapturedContext: false);
	}

	private void OnDataAvailable(object? sender, WaveInEventArgs e)
	{
		if (!Monitor.TryEnter(_sync))
		{
			return;
		}
		try
		{
			if (_buffer != null && _settings.Enabled)
			{
				int num = 0;
				int num2 = e.BytesRecorded;
				if (_dropBytesRemaining > 0)
				{
					int bytes = Math.Min(_dropBytesRemaining, num2);
					bytes = AlignBytes(bytes, _capture?.WaveFormat.BlockAlign ?? 1);
					_dropBytesRemaining -= bytes;
					num += bytes;
					num2 -= bytes;
				}
				if (num2 > 0)
				{
					_buffer.AddSamples(e.Buffer, num, num2);
					_receivedBytes += num2;
					_receivedBuffers++;
					MaybeLogAudioTelemetry();
				}
			}
		}
		finally
		{
			Monitor.Exit(_sync);
		}
	}

	private void OnRecordingStopped(object? sender, StoppedEventArgs e)
	{
		if (e.Exception != null)
		{
			_logger.Error("Audio capture stopped unexpectedly.", e.Exception);
		}
	}

	private void StopInternal()
	{
		IsRunning = false;
		if (_capture != null)
		{
			_capture.DataAvailable -= OnDataAvailable;
			_capture.RecordingStopped -= OnRecordingStopped;
		}
		try
		{
			_capture?.StopRecording();
		}
		catch (Exception exception)
		{
			_logger.Error("Stopping audio capture failed.", exception);
		}
		try
		{
			_output?.Stop();
		}
		catch (Exception exception2)
		{
			_logger.Error("Stopping audio output failed.", exception2);
		}
		_capture?.Dispose();
		_output?.Dispose();
		_capture = null;
		_output = null;
		_buffer = null;
		_volumeProvider = null;
		_dropBytesRemaining = 0;
		_receivedBytes = 0L;
		_receivedBuffers = 0;
	}

	private ISampleProvider MatchOutputFormat(ISampleProvider samples, WaveFormat outputMixFormat)
	{
		if (samples.WaveFormat.SampleRate != outputMixFormat.SampleRate)
		{
			_logger.Info($"Audio resampling enabled: {samples.WaveFormat.SampleRate} Hz -> {outputMixFormat.SampleRate} Hz.");
			samples = new WdlResamplingSampleProvider(samples, outputMixFormat.SampleRate);
		}
		if (samples.WaveFormat.Channels == 1 && outputMixFormat.Channels == 2)
		{
			samples = new MonoToStereoSampleProvider(samples);
		}
		else if (samples.WaveFormat.Channels == 2 && outputMixFormat.Channels == 1)
		{
			samples = new StereoToMonoSampleProvider(samples);
		}
		else if (samples.WaveFormat.Channels != outputMixFormat.Channels)
		{
			_logger.Info($"Audio channel count differs from output mix ({samples.WaveFormat.Channels} -> {outputMixFormat.Channels}); using source channel layout.");
		}
		return samples;
	}

	private void MaybeLogAudioTelemetry()
	{
		DateTimeOffset utcNow = DateTimeOffset.UtcNow;
		if (!(utcNow - _lastTelemetryUtc < TimeSpan.FromSeconds(1.0)))
		{
			int value = _buffer?.BufferedBytes ?? 0;
			string value2 = _output?.PlaybackState.ToString() ?? "stopped";
			_logger.Info($"Audio activity: {_receivedBuffers} buffers, {_receivedBytes} bytes captured, {value} bytes buffered, playback={value2}.");
			_lastTelemetryUtc = utcNow;
			_receivedBytes = 0L;
			_receivedBuffers = 0;
		}
	}

	private static string DescribeWaveFormat(WaveFormat format)
	{
		return $"{format.Encoding}, {format.SampleRate} Hz, {format.BitsPerSample}-bit, {format.Channels} ch";
	}

	private static int AlignBytes(int bytes, int blockAlign)
	{
		if (blockAlign <= 1)
		{
			return Math.Max(0, bytes);
		}
		return Math.Max(0, bytes - bytes % blockAlign);
	}

	private static AudioSettings CopySettings(AudioSettings settings)
	{
		return new AudioSettings
		{
			Enabled = settings.Enabled,
			Volume = settings.Volume,
			Muted = settings.Muted,
			DelayMilliseconds = settings.DelayMilliseconds,
			ChannelMode = settings.ChannelMode
		};
	}
}
