namespace MirrorDeck.Core.Models;

public sealed record VideoDeviceInfo(string Id, string Name)
{
	public override string ToString()
	{
		return Name;
	}
}
