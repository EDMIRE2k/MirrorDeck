using MirrorDeck.Core.Models;

namespace MirrorDeck.App.ViewModels;

public sealed record CaptureSourceTypeOption(string Name, CaptureSourceType Value)
{
	public override string ToString()
	{
		return Name;
	}
}
