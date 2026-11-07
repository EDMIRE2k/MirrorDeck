using System;

namespace MirrorDeck.Core.Services;

public interface IAppLogger
{
	string LogPath { get; }

	event EventHandler<AppLogEntryEventArgs>? MessageLogged;

	void Info(string message);

	void Error(string message, Exception? exception = null);
}
