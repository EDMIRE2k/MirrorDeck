using System;
using System.IO;
using System.Threading.Tasks;
using MirrorDeck.Core.Services;

namespace MirrorDeck.Settings.Persistence;

public sealed class FileAppLogger : IAppLogger
{
	private readonly object _sync = new object();

	public string LogPath { get; }

	public event EventHandler<AppLogEntryEventArgs>? MessageLogged;

	public FileAppLogger(string logPath)
	{
		LogPath = logPath;
		Directory.CreateDirectory(Path.GetDirectoryName(LogPath));
	}

	public static FileAppLogger CreateDefault()
	{
		return new FileAppLogger(MirrorDeckStorage.GetLogPath());
	}

	public void Info(string message)
	{
		Write("INFO", message, null);
	}

	public void Error(string message, Exception? exception = null)
	{
		Write("ERROR", message, exception);
	}

	private void Write(string level, string message, Exception? exception)
	{
		string line = $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff zzz} [{level}] {message}";
		MessageLogged?.Invoke(this, new AppLogEntryEventArgs(line));
		Task.Run(() =>
		{
			lock (_sync)
			{
				File.AppendAllText(LogPath, line + Environment.NewLine);
				if (exception != null)
				{
					File.AppendAllText(LogPath, exception?.ToString() + Environment.NewLine);
				}
			}
		});
	}
}
