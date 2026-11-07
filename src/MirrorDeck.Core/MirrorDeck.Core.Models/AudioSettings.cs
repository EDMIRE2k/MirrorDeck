namespace MirrorDeck.Core.Models;

public sealed class AudioSettings
{
	public bool Enabled { get; set; } = true;

	public double Volume { get; set; } = 1.0;

	public bool Muted { get; set; }

	public int DelayMilliseconds { get; set; }

	public AudioChannelMode ChannelMode { get; set; }
}
