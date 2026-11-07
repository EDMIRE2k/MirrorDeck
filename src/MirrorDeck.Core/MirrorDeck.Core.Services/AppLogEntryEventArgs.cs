using System;

namespace MirrorDeck.Core.Services;

public sealed class AppLogEntryEventArgs : EventArgs
{
	public string Line { get; }

	public AppLogEntryEventArgs(string line)
	{
		Line = line;
	}
}
