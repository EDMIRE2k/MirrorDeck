namespace MirrorDeck.Core.Models;

public sealed record AudioDeviceInfo(string Id, string Name, AudioDeviceDirection Direction)
{
	public override string ToString()
	{
		return Name;
	}
}
