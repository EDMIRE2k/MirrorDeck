using System;
using MirrorDeck.Settings.Persistence;

namespace MirrorDeck.PreviewHost;

internal sealed class PreviewHostLogger : IDisposable
{
	private readonly FileAppLogger _logger = FileAppLogger.CreateDefault();

	public static PreviewHostLogger Create()
	{
		return new PreviewHostLogger();
	}

	public void Info(string message)
	{
		_logger.Info("[PreviewHost] " + message);
	}

	public void Error(string message, Exception? exception = null)
	{
		_logger.Error("[PreviewHost] " + message, exception);
	}

	public void Dispose()
	{
	}
}
